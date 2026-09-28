// Harder tasks for bench2.cjs. Exports (h) => tasks, where h carries the shared helpers.
module.exports = (h) => {
  const { F, firstCode, runChecks, vm } = h

  // ================= H1: query-filter compiler (shot) =================
  const T = (desc, filter, obj, expected) => ({ desc, expr: `compileFilter(${JSON.stringify(filter)})(${JSON.stringify(obj)})`, eq: expected })
  const X = (filter) => ({ desc: 'throws on ' + JSON.stringify(filter), expr: `compileFilter(${JSON.stringify(filter)})`, throws: true })
  const filterChecks = [
    T('term match', 'status:open', { status: 'open' }, true), T('term miss', 'status:open', { status: 'closed' }, false),
    T('numeric equals string', 'n:3', { n: 3 }, true), T('quoted value', 'name:"Ann Lee"', { name: 'Ann Lee' }, true),
    T('escaped quote', 't:"say \\"hi\\""', { t: 'say "hi"' }, true),
    T('dotted path', 'user.name:bob', { user: { name: 'bob' } }, true), T('missing nested', 'user.name:bob', { user: {} }, false), T('missing top', 'user.name:bob', {}, false),
    T('>= true', 'age>=18', { age: 18 }, true), T('>= false', 'age>=18', { age: 17 }, false), T('< true', 'age<18', { age: 17 }, true),
    T('<= true', 'age<=17', { age: 17 }, true), T('> strict', 'age>18', { age: 18 }, false),
    T('non-number field', 'age>1', { age: 'abc' }, false), T('numeric string is not a number', 'age>1', { age: '20' }, false), T('missing field compare', 'age>1', {}, false),
    T('negative number', 't<-5', { t: -10 }, true), T('decimal', 'p>=1.5', { p: 1.5 }, true),
    T('NOT', 'NOT status:open', { status: 'closed' }, true),
    T('OR below AND (a)', 'a:1 OR b:2 AND c:3', { a: 1, b: 9, c: 9 }, true), T('OR below AND (b)', 'a:1 OR b:2 AND c:3', { a: 9, b: 2, c: 9 }, false), T('OR below AND (c)', 'a:1 OR b:2 AND c:3', { a: 9, b: 2, c: 3 }, true),
    T('parens override', '(a:1 OR b:2) AND c:3', { a: 1, b: 0, c: 9 }, false), T('parens ok', '(a:1 OR b:2) AND c:3', { a: 1, c: 3 }, true),
    T('keywords case-insensitive', 'a:1 and not b:2', { a: 1, b: 3 }, true), T('keywords case-insensitive 2', 'a:1 and not b:2', { a: 1, b: 2 }, false),
    T('NOT binds tightest', 'NOT a:1 AND b:2', { a: 1, b: 9 }, false), T('double NOT', 'NOT NOT a:1', { a: 1 }, true), T('left-assoc OR', 'a:1 OR b:2 OR c:3', { c: 3 }, true),
    T('whitespace', '  a:1   AND   b:2 ', { a: 1, b: 2 }, true), T('tight parens', '(a:1)AND(b:2)', { a: 1, b: 2 }, true),
    T('value with dash', 'tag:a-b', { tag: 'a-b' }, true), T('keyword as value', 'x:and', { x: 'and' }, true), T('keyword inside quotes', 't:"a AND b"', { t: 'a AND b' }, true),
    ...['', '   ', 'a:1 AND', 'AND a:1', '(a:1', 'a:1)', 'a:1 b:2', 'a:"open', 'a:1 OR OR b:2', '@', 'NOT', ':1', 'a:', 'a>', 'a>x', '()'].map(X),
    { desc: 'no eval/Function', notContains: ['eval(', 'new Function', 'Function('] },
  ]
  const filterRef = `${F}javascript
function compileFilter(src) {
  const toks = []; let i = 0; const n = src.length;
  while (i < n) {
    const c = src[i];
    if (/\\s/.test(c)) { i++; continue; }
    if (c === '(' || c === ')') { toks.push({ t: c }); i++; continue; }
    const m = /^[A-Za-z0-9_.]+/.exec(src.slice(i));
    if (!m) throw new Error('unexpected ' + c);
    const word = m[0]; let j = i + word.length;
    const op = /^(>=|<=|>|<|:)/.exec(src.slice(j));
    if (!op) {
      const u = word.toUpperCase();
      if (u === 'AND' || u === 'OR' || u === 'NOT') { toks.push({ t: u }); i = j; continue; }
      throw new Error('bad word ' + word);
    }
    j += op[0].length;
    if (op[0] === ':') {
      let val;
      if (src[j] === '"') {
        let k = j + 1, s = '';
        for (;;) {
          if (k >= n) throw new Error('unterminated');
          if (src[k] === '\\\\' && k + 1 < n) { s += src[k + 1]; k += 2; continue; }
          if (src[k] === '"') { k++; break; }
          s += src[k++];
        }
        val = s; j = k;
      } else {
        const vm = /^[^\\s()"]+/.exec(src.slice(j));
        if (!vm) throw new Error('missing value');
        val = vm[0]; j += val.length;
      }
      toks.push({ t: 'term', field: word, op: ':', val });
    } else {
      const nm = /^-?\\d+(\\.\\d+)?/.exec(src.slice(j));
      if (!nm) throw new Error('need number');
      toks.push({ t: 'term', field: word, op: op[0], val: parseFloat(nm[0]) });
      j += nm[0].length;
    }
    i = j;
  }
  if (!toks.length) throw new Error('empty');
  let p = 0;
  const peek = () => toks[p];
  const mk = (t) => (obj) => {
    let v = obj;
    for (const part of t.field.split('.')) { if (v == null || typeof v !== 'object') return false; v = v[part]; }
    if (v === undefined) return false;
    if (t.op === ':') return String(v) === t.val;
    if (typeof v !== 'number') return false;
    return t.op === '>' ? v > t.val : t.op === '>=' ? v >= t.val : t.op === '<' ? v < t.val : v <= t.val;
  };
  function parseOr() { let l = parseAnd(); while (peek() && peek().t === 'OR') { p++; const a = l, r = parseAnd(); l = (o) => a(o) || r(o); } return l; }
  function parseAnd() { let l = parseNot(); while (peek() && peek().t === 'AND') { p++; const a = l, r = parseNot(); l = (o) => a(o) && r(o); } return l; }
  function parseNot() { if (peek() && peek().t === 'NOT') { p++; const x = parseNot(); return (o) => !x(o); } return parsePrimary(); }
  function parsePrimary() {
    const t = peek();
    if (!t) throw new Error('unexpected end');
    if (t.t === '(') { p++; const e = parseOr(); if (!peek() || peek().t !== ')') throw new Error('missing )'); p++; return e; }
    if (t.t === 'term') { p++; return mk(t); }
    throw new Error('unexpected ' + t.t);
  }
  const f = parseOr();
  if (p < toks.length) throw new Error('trailing input');
  return f;
}
${F}`

  // ================= H2: behaviour-preserving refactor (shot) =================
  const pricingSrc = `// Prices an order. All money values are integer cents.
function priceOrder(order) {
  const items = order.items.filter((it) => it.qty > 0);
  if (items.length === 0) {
    return { subtotal: 0, discounts: { quantity: 0, tier: 0, coupon: 0 }, shipping: 0, tax: 0, total: 0 };
  }
  let subtotal = 0;
  let quantityDiscount = 0;
  for (const it of items) {
    const line = it.price * it.qty;
    subtotal += line;
    if (it.qty >= 50) {
      quantityDiscount += Math.floor((line * 20) / 100);
    } else if (it.qty >= 10) {
      quantityDiscount += Math.floor((line * 10) / 100);
    }
  }
  let amount = subtotal - quantityDiscount;
  let tierPct = 0;
  if (order.customer.tier === 'gold') {
    tierPct = 5;
  } else if (order.customer.tier === 'silver') {
    tierPct = 2;
  }
  if (tierPct > 0 && order.customer.ordersCount >= 20) {
    tierPct += 1;
  }
  const tierDiscount = Math.floor((amount * tierPct) / 100);
  amount -= tierDiscount;
  let couponDiscount = 0;
  let freeShipping = false;
  if (order.coupon === 'SAVE10') {
    couponDiscount = Math.min(Math.floor(amount / 10), 5000);
  } else if (order.coupon === 'FLAT5') {
    if (amount >= 2500) {
      couponDiscount = 500;
    }
  } else if (order.coupon === 'FREESHIP') {
    freeShipping = true;
  }
  amount -= couponDiscount;
  let baseShipping;
  if (order.country === 'US') {
    baseShipping = 500;
  } else if (order.country === 'CA') {
    baseShipping = 800;
  } else {
    baseShipping = 1500;
  }
  let shipping = 0;
  if (!(freeShipping || amount >= 10000)) {
    shipping += baseShipping;
  }
  if (order.expedited) {
    shipping += baseShipping;
  }
  let taxable = amount;
  let rate;
  if (order.country === 'US') {
    rate = 8;
  } else if (order.country === 'CA') {
    rate = 13;
  } else if (order.country === 'DE') {
    rate = 19;
    taxable += shipping;
  } else {
    rate = 0;
  }
  const tax = Math.round((taxable * rate) / 100);
  const total = amount + shipping + tax;
  return {
    subtotal,
    discounts: { quantity: quantityDiscount, tier: tierDiscount, coupon: couponDiscount },
    shipping,
    tax,
    total,
  };
}

module.exports = { priceOrder };
`
  const pricingRef = `const QUANTITY_TIERS = [[50, 20], [10, 10]];

function activeItems(order) {
  return order.items.filter((it) => it.qty > 0);
}

function lineDiscount(line, qty) {
  for (const [minQty, pct] of QUANTITY_TIERS) {
    if (qty >= minQty) return Math.floor((line * pct) / 100);
  }
  return 0;
}

function summarizeItems(items) {
  let subtotal = 0;
  let quantityDiscount = 0;
  for (const it of items) {
    const line = it.price * it.qty;
    subtotal += line;
    quantityDiscount += lineDiscount(line, it.qty);
  }
  return { subtotal, quantityDiscount };
}

function tierPercent(customer) {
  let pct = 0;
  if (customer.tier === 'gold') pct = 5;
  else if (customer.tier === 'silver') pct = 2;
  if (pct > 0 && customer.ordersCount >= 20) pct += 1;
  return pct;
}

function couponEffect(coupon, amount) {
  if (coupon === 'SAVE10') return { discount: Math.min(Math.floor(amount / 10), 5000), freeShipping: false };
  if (coupon === 'FLAT5') return { discount: amount >= 2500 ? 500 : 0, freeShipping: false };
  if (coupon === 'FREESHIP') return { discount: 0, freeShipping: true };
  return { discount: 0, freeShipping: false };
}

function baseShipping(country) {
  if (country === 'US') return 500;
  if (country === 'CA') return 800;
  return 1500;
}

function shippingCost(country, amount, freeShipping, expedited) {
  const base = baseShipping(country);
  let shipping = 0;
  if (!(freeShipping || amount >= 10000)) shipping += base;
  if (expedited) shipping += base;
  return shipping;
}

function taxFor(country, amount, shipping) {
  if (country === 'US') return Math.round((amount * 8) / 100);
  if (country === 'CA') return Math.round((amount * 13) / 100);
  if (country === 'DE') return Math.round(((amount + shipping) * 19) / 100);
  return 0;
}

function priceOrder(order) {
  const items = activeItems(order);
  if (items.length === 0) {
    return { subtotal: 0, discounts: { quantity: 0, tier: 0, coupon: 0 }, shipping: 0, tax: 0, total: 0 };
  }
  const { subtotal, quantityDiscount } = summarizeItems(items);
  let amount = subtotal - quantityDiscount;
  const tierDiscount = Math.floor((amount * tierPercent(order.customer)) / 100);
  amount -= tierDiscount;
  const coupon = couponEffect(order.coupon, amount);
  amount -= coupon.discount;
  const shipping = shippingCost(order.country, amount, coupon.freeShipping, order.expedited);
  const tax = taxFor(order.country, amount, shipping);
  return {
    subtotal,
    discounts: { quantity: quantityDiscount, tier: tierDiscount, coupon: coupon.discount },
    shipping,
    tax,
    total: amount + shipping + tax,
  };
}

module.exports = { priceOrder };
`
  const pricingSubtle = pricingSrc.replace('} else if (it.qty >= 10) {', '} else if (it.qty > 10) {')
  const canon = (v) => JSON.stringify(v, (k, x) => x && typeof x === 'object' && !Array.isArray(x) ? Object.keys(x).sort().reduce((o, key) => (o[key] = x[key], o), {}) : x)
  const origMod = { exports: {} }
  vm.runInContext(pricingSrc, vm.createContext({ module: origMod, exports: origMod.exports }))
  const origFn = origMod.exports.priceOrder
  let seed = 12345
  const rnd = () => { seed = (seed * 1103515245 + 12345) % 2147483648; return seed / 2147483648 }
  const pick = (a) => a[Math.floor(rnd() * a.length)]
  const orders = Array.from({ length: 200 }, () => {
    const o = {
      items: Array.from({ length: Math.floor(rnd() * 5) }, () => ({ sku: 'S' + Math.floor(rnd() * 99), price: 50 + Math.floor(rnd() * 9000), qty: pick([0, 1, 2, 5, 9, 10, 11, 49, 50, 51, 120]) })),
      country: pick(['US', 'CA', 'DE', 'FR', 'JP']), customer: { tier: pick(['none', 'silver', 'gold']), ordersCount: Math.floor(rnd() * 40) }, expedited: rnd() < 0.4,
    }
    const c = pick([undefined, 'SAVE10', 'FLAT5', 'FREESHIP', 'BOGUS']); if (c) o.coupon = c
    return o
  })
  const mk = (items, country, tier, cnt, coupon, exp) => ({ items, country, customer: { tier, ordersCount: cnt }, ...(coupon ? { coupon } : {}), expedited: !!exp })
  const edge = [
    ['empty order', mk([], 'US', 'none', 0)],
    ['all quantities zero', mk([{ sku: 'a', price: 500, qty: 0 }], 'US', 'gold', 30, 'SAVE10')],
    ['exactly 100.00 with expedited', mk([{ sku: 'a', price: 10000, qty: 1 }], 'US', 'none', 0, null, true)],
    ['FREESHIP with expedited', mk([{ sku: 'a', price: 900, qty: 1 }], 'CA', 'none', 0, 'FREESHIP', true)],
    ['DE tax includes shipping', mk([{ sku: 'a', price: 3000, qty: 1 }], 'DE', 'none', 0)],
    ['SAVE10 cap', mk([{ sku: 'a', price: 100000, qty: 1 }], 'US', 'none', 0, 'SAVE10')],
    ['FLAT5 at 2499', mk([{ sku: 'a', price: 2499, qty: 1 }], 'US', 'none', 0, 'FLAT5')],
    ['FLAT5 at 2500', mk([{ sku: 'a', price: 2500, qty: 1 }], 'US', 'none', 0, 'FLAT5')],
    ['gold with 20 orders', mk([{ sku: 'a', price: 7777, qty: 3 }], 'CA', 'gold', 20)],
    ['no tier with 50 orders', mk([{ sku: 'a', price: 7777, qty: 3 }], 'FR', 'none', 50)],
    ['quantity boundaries 9/10/49/50', mk([{ sku: 'a', price: 333, qty: 9 }, { sku: 'b', price: 333, qty: 10 }, { sku: 'c', price: 333, qty: 49 }, { sku: 'd', price: 333, qty: 50 }], 'JP', 'silver', 5)],
    ['unknown coupon', mk([{ sku: 'a', price: 1234, qty: 2 }], 'US', 'silver', 1, 'BOGUS')],
  ]
  const same = (expr) => `(()=>{ const P = module.exports.priceOrder; const c = (x) => JSON.parse(JSON.stringify(x)); return ${expr} })()`
  const refactorChecks = [
    ...edge.map(([desc], i) => ({ desc: 'same result: ' + desc, expr: same(`__canon(P(c(__edge[${i}]))) === __canon(__orig(c(__edge[${i}])))`), eq: true })),
    ...Array.from({ length: 8 }, (_, g) => ({ desc: `same result on random orders ${g * 25 + 1}-${g * 25 + 25}`, expr: same(`__orders.slice(${g * 25}, ${g * 25 + 25}).every((o) => __canon(P(c(o))) === __canon(__orig(c(o))))`), eq: true })),
    { desc: 'does not modify its input', expr: same('(()=>{ const o = c(__edge[10]); const before = __canon(o); P(o); return __canon(o) === before })()'), eq: true },
    { desc: 'split into at least four helper functions', pred: (t) => (t.match(/\bfunction\s+\w+\s*\(/g) || []).length + (t.match(/\bconst\s+\w+\s*=\s*(\([^)]*\)|\w+)\s*=>/g) || []).length >= 5 },
  ]
  const refactorCtx = (code) => () => {
    const mod = { exports: {} }
    const ctx = vm.createContext({ module: mod, exports: mod.exports, console, __orig: origFn, __canon: canon, __orders: orders, __edge: edge.map(e => e[1]) })
    vm.runInContext(code, ctx, { timeout: 3000 })
    return ctx
  }

  // ================= H3: hard agent (multi-module library, subtle bugs) =================
  const src = {
    'src/dates.js': `/** True if y is a leap year (Gregorian rules: divisible by 4, except centuries unless divisible by 400). */
function isLeap(y) {
  return y % 4 === 0 && y % 100 !== 0;
}

/** Number of days in month m (1-12) of year y. */
function daysInMonth(y, m) {
  return [31, isLeap(y) ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31][m - 1];
}

/** Day of week for a date {y, m, d}: 0 = Sunday ... 6 = Saturday. m is 1-12. */
function dayOfWeek(date) {
  return new Date(Date.UTC(date.y, date.m, date.d)).getUTCDay();
}

/** Add n months (may be negative) to a date {y, m, d}. If the day does not exist in the target month, use the last day of that month. */
function addMonths(date, n) {
  const total = date.y * 12 + (date.m - 1) + n;
  return { y: Math.floor(total / 12), m: (total % 12) + 1, d: date.d };
}

/** Add n days (may be negative) to a date {y, m, d}. */
function addDays(date, n) {
  const t = new Date(Date.UTC(date.y, date.m - 1, date.d + n));
  return { y: t.getUTCFullYear(), m: t.getUTCMonth() + 1, d: t.getUTCDate() };
}

/** True if the date falls on a Saturday or Sunday. */
function isWeekend(date) {
  const w = dayOfWeek(date);
  return w === 0 || w === 6;
}

/** The first business day (Monday to Friday) on or after the date. */
function nextBusinessDay(date) {
  let cur = { y: date.y, m: date.m, d: date.d };
  while (isWeekend(cur)) {
    cur = addDays(cur, 1);
  }
  return cur;
}

module.exports = { isLeap, daysInMonth, dayOfWeek, addMonths, addDays, isWeekend, nextBusinessDay };
`,
    'src/money.js': `/** Split an amount in cents into \`parts\` shares that differ by at most one cent and add up exactly to the amount. Earlier shares get the extra cents. splitEvenly(100, 3) -> [34, 33, 33]. */
function splitEvenly(cents, parts) {
  const base = Math.floor(cents / parts);
  return Array.from({ length: parts }, () => base);
}

/** pct percent of an amount in cents, rounded half up to a whole cent. applyPercent(1005, 15) -> 151. */
function applyPercent(cents, pct) {
  return Math.floor((cents * pct) / 100);
}

/** Format cents as a string like "$12.34". */
function formatCents(cents) {
  return '$' + (cents / 100).toFixed(2);
}

/** Sum an array of cent amounts. */
function sum(values) {
  return values.reduce((a, b) => a + b, 0);
}

module.exports = { splitEvenly, applyPercent, formatCents, sum };
`,
    'src/invoice.js': `const { applyPercent, sum } = require('./money');

/** Subtotal in cents: the sum of line.cents over all lines. */
function subtotal(lines) {
  return sum(lines.map((l) => l.cents));
}

/** Tax in cents on the whole invoice subtotal (not line by line). rate is a fraction such as 0.07. Rounded half up to a whole cent. */
function tax(lines, rate) {
  return sum(lines.map((l) => Math.floor(l.cents * rate)));
}

/** Total in cents: subtotal plus tax. */
function total(lines, rate) {
  return subtotal(lines) + tax(lines, rate);
}

module.exports = { subtotal, tax, total };
`,
    'src/schedule.js': `const { addMonths, nextBusinessDay } = require('./dates');

/**
 * The dates of \`count\` monthly payments starting at \`start\` ({y, m, d}).
 * Payment i is the start date plus i months. It keeps the original day of the month where the month is long
 * enough, otherwise it falls on the last day of that month: 31 Jan, 29 Feb, 31 Mar, 30 Apr (in a leap year).
 */
function monthlyDates(start, count) {
  const out = [start];
  for (let i = 1; i < count; i++) {
    out.push(addMonths(out[i - 1], 1));
  }
  return out;
}

/** Like monthlyDates, but any payment that falls on a weekend moves to the following Monday. */
function businessMonthlyDates(start, count) {
  return monthlyDates(start, count).map(nextBusinessDay);
}

module.exports = { monthlyDates, businessMonthlyDates };
`,
  }
  const fixed = {
    'src/dates.js': src['src/dates.js']
      .replace('return y % 4 === 0 && y % 100 !== 0;', 'return (y % 4 === 0 && y % 100 !== 0) || y % 400 === 0;')
      .replace('Date.UTC(date.y, date.m, date.d)).getUTCDay()', 'Date.UTC(date.y, date.m - 1, date.d)).getUTCDay()')
      .replace('return { y: Math.floor(total / 12), m: (total % 12) + 1, d: date.d };', 'const y = Math.floor(total / 12), m = (total % 12) + 1;\n  return { y, m, d: Math.min(date.d, daysInMonth(y, m)) };'),
    'src/money.js': src['src/money.js']
      .replace('const base = Math.floor(cents / parts);\n  return Array.from({ length: parts }, () => base);', 'const base = Math.floor(cents / parts);\n  const extra = cents - base * parts;\n  return Array.from({ length: parts }, (_, i) => base + (i < extra ? 1 : 0));')
      .replace('return Math.floor((cents * pct) / 100);', 'return Math.round((cents * pct) / 100);'),
    'src/invoice.js': src['src/invoice.js'].replace('return sum(lines.map((l) => Math.floor(l.cents * rate)));', 'return applyPercent(subtotal(lines), rate * 100);'),
    'src/schedule.js': src['src/schedule.js'].replace('out.push(addMonths(out[i - 1], 1));', 'out.push(addMonths(start, i));'),
  }
  const visibleTest = `const test = require('node:test');
const assert = require('node:assert');
const dates = require('../src/dates');
const money = require('../src/money');
const invoice = require('../src/invoice');
const schedule = require('../src/schedule');

test('isLeap for ordinary years', () => {
  assert.strictEqual(dates.isLeap(2024), true);
  assert.strictEqual(dates.isLeap(2023), false);
});

test('addMonths within a year', () => {
  assert.deepStrictEqual(dates.addMonths({ y: 2024, m: 1, d: 15 }, 1), { y: 2024, m: 2, d: 15 });
});

test('splitEvenly with no remainder', () => {
  assert.deepStrictEqual(money.splitEvenly(90, 3), [30, 30, 30]);
});

test('splitEvenly spreads the remainder', () => {
  assert.deepStrictEqual(money.splitEvenly(100, 3), [34, 33, 33]);
});

test('applyPercent rounds half up', () => {
  assert.strictEqual(money.applyPercent(1005, 15), 151);
});

test('invoice total for a single line', () => {
  assert.strictEqual(invoice.total([{ cents: 1000 }], 0.07), 1070);
});

test('monthlyDates on the 15th', () => {
  assert.deepStrictEqual(schedule.monthlyDates({ y: 2024, m: 1, d: 15 }, 3), [{ y: 2024, m: 1, d: 15 }, { y: 2024, m: 2, d: 15 }, { y: 2024, m: 3, d: 15 }]);
});
`
  const D = (y, m, d) => `{ y: ${y}, m: ${m}, d: ${d} }`
  const hid = []
  const t = (name, body) => hid.push(`test(${JSON.stringify(name)}, () => { ${body} });`)
  t('isLeap 2000', 'assert.strictEqual(dates.isLeap(2000), true)')
  t('isLeap 1900', 'assert.strictEqual(dates.isLeap(1900), false)')
  t('isLeap 2024 and 2023', 'assert.strictEqual(dates.isLeap(2024), true); assert.strictEqual(dates.isLeap(2023), false)')
  t('daysInMonth Feb 2000', 'assert.strictEqual(dates.daysInMonth(2000, 2), 29)')
  t('daysInMonth Feb 2023', 'assert.strictEqual(dates.daysInMonth(2023, 2), 28)')
  t('dayOfWeek Friday', `assert.strictEqual(dates.dayOfWeek(${D(2024, 3, 15)}), 5)`)
  t('dayOfWeek Sunday', `assert.strictEqual(dates.dayOfWeek(${D(2024, 6, 16)}), 0)`)
  t('addMonths clamps to Feb 29', `assert.deepStrictEqual(dates.addMonths(${D(2024, 1, 31)}, 1), ${D(2024, 2, 29)})`)
  t('addMonths clamps to Feb 28', `assert.deepStrictEqual(dates.addMonths(${D(2023, 1, 31)}, 1), ${D(2023, 2, 28)})`)
  t('addMonths negative with clamp', `assert.deepStrictEqual(dates.addMonths(${D(2024, 3, 31)}, -1), ${D(2024, 2, 29)})`)
  t('addMonths across a year end', `assert.deepStrictEqual(dates.addMonths(${D(2024, 11, 15)}, 3), ${D(2025, 2, 15)})`)
  t('addMonths negative across a year start', `assert.deepStrictEqual(dates.addMonths(${D(2024, 1, 15)}, -1), ${D(2023, 12, 15)})`)
  t('addDays', `assert.deepStrictEqual(dates.addDays(${D(2024, 2, 28)}, 2), ${D(2024, 3, 1)})`)
  t('nextBusinessDay from Saturday', `assert.deepStrictEqual(dates.nextBusinessDay(${D(2024, 3, 16)}), ${D(2024, 3, 18)})`)
  t('nextBusinessDay on a weekday', `assert.deepStrictEqual(dates.nextBusinessDay(${D(2024, 3, 15)}), ${D(2024, 3, 15)})`)
  t('splitEvenly 10 into 4', 'assert.deepStrictEqual(money.splitEvenly(10, 4), [3, 3, 2, 2])')
  t('splitEvenly 7 into 7', 'assert.deepStrictEqual(money.splitEvenly(7, 7), [1, 1, 1, 1, 1, 1, 1])')
  t('splitEvenly zero', 'assert.deepStrictEqual(money.splitEvenly(0, 3), [0, 0, 0])')
  t('applyPercent rounds up', 'assert.strictEqual(money.applyPercent(999, 10), 100)')
  t('applyPercent exact', 'assert.strictEqual(money.applyPercent(1000, 7), 70)')
  t('applyPercent zero', 'assert.strictEqual(money.applyPercent(0, 50), 0)')
  t('formatCents', "assert.strictEqual(money.formatCents(1234), '$12.34')")
  t('sum', 'assert.strictEqual(money.sum([1, 2, 3]), 6)')
  t('tax is on the subtotal, not per line', 'assert.strictEqual(invoice.tax([{ cents: 333 }, { cents: 333 }, { cents: 334 }], 0.07), 70)')
  t('total with several lines', 'assert.strictEqual(invoice.total([{ cents: 333 }, { cents: 333 }, { cents: 334 }], 0.07), 1070)')
  t('total of no lines', 'assert.strictEqual(invoice.total([], 0.1), 0)')
  t('monthlyDates keeps clamping from the start day', `assert.deepStrictEqual(schedule.monthlyDates(${D(2024, 1, 31)}, 4), [${D(2024, 1, 31)}, ${D(2024, 2, 29)}, ${D(2024, 3, 31)}, ${D(2024, 4, 30)}])`)
  t('monthlyDates single', `assert.deepStrictEqual(schedule.monthlyDates(${D(2024, 1, 15)}, 1), [${D(2024, 1, 15)}])`)
  t('businessMonthlyDates moves weekends to Monday', `assert.deepStrictEqual(schedule.businessMonthlyDates(${D(2024, 3, 15)}, 4), [${D(2024, 3, 15)}, ${D(2024, 4, 15)}, ${D(2024, 5, 15)}, ${D(2024, 6, 17)}])`)
  const hiddenTest = `const test = require('node:test');
const assert = require('node:assert');
const dates = require('../src/dates');
const money = require('../src/money');
const invoice = require('../src/invoice');
const schedule = require('../src/schedule');

${hid.join('\n')}
`

  const agentTools = [
    { type: 'function', function: { name: 'list_files', description: 'List files and folders in a project directory', parameters: { type: 'object', properties: { path: { type: 'string', description: 'Project-relative directory, use "." for the root' } }, required: ['path'] } } },
    { type: 'function', function: { name: 'read_file', description: 'Read a text file from the project', parameters: { type: 'object', properties: { path: { type: 'string', description: 'Project-relative path' } }, required: ['path'] } } },
    { type: 'function', function: { name: 'write_file', description: 'Replace the entire content of a project file', parameters: { type: 'object', properties: { path: { type: 'string' }, content: { type: 'string', description: 'The complete new file content' } }, required: ['path', 'content'] } } },
    { type: 'function', function: { name: 'replace_in_file', description: 'Replace one exact occurrence of a text snippet in a file. The snippet must appear exactly once.', parameters: { type: 'object', properties: { path: { type: 'string' }, old_text: { type: 'string', description: 'Exact text to replace' }, new_text: { type: 'string', description: 'Replacement text' } }, required: ['path', 'old_text', 'new_text'] } } },
    { type: 'function', function: { name: 'run_tests', description: 'Run the project test suite and return the output', parameters: { type: 'object', properties: {} } } },
  ]

  const tasks = [
    { id: 'H1-filter', kind: 'shot',
      prompt: `Write a JavaScript function compileFilter(expr) that compiles a search filter string into a predicate function. The predicate takes a plain object and returns true or false.

Syntax:
- A term is field:value. It is true when String(value of field) === value (exact, case-sensitive). The value is either an unquoted run of characters that are not whitespace, parentheses or double quotes, or a double-quoted string that may contain spaces and may contain \\" for a literal quote.
- Comparison terms field>n, field>=n, field<n, field<=n, where n is a number (optionally negative or decimal). They are true only when the field's value is a JavaScript number that satisfies the comparison. A missing field, or a value that is not a number (including a numeric string), makes the term false.
- A field name is a dotted path such as user.name made of letters, digits, underscores and dots. If any part of the path is missing the term is false.
- Operators: NOT, AND, OR, and parentheses. The keywords are case-insensitive. NOT binds tightest, then AND, then OR; AND and OR are left-associative. Whitespace between tokens is ignored, and no whitespace is needed next to parentheses.
- Two terms side by side with no operator between them are an error.
- Any invalid expression (empty, unbalanced parentheses, a missing operand, a dangling operator, an unterminated quote, an unknown character, a missing field, a missing value, a non-numeric comparison value, side-by-side terms) must make compileFilter itself throw an Error, not the predicate.
Do not use eval, Function or any other dynamic code execution. Output only the code in one ${F}javascript block, no explanation.`,
      score: (text) => { const c = firstCode(text); return runChecks(h.globalCtx(c, 'compileFilter'), filterChecks, c) }, ref: filterRef, wrong: `${F}javascript\nfunction compileFilter(s){return ()=>false}\n${F}` },
    { id: 'H2-refactor', kind: 'shot',
      prompt: `Refactor the JavaScript function below into several small, well-named helper functions (at least four helpers besides priceOrder). It must keep behaving exactly the same for every possible input: same results, same rounding, same result object shape. Do not fix or change any behaviour, even if something looks odd. Do not modify the input order. Keep the export module.exports = { priceOrder }. Return the complete refactored file in one ${F}javascript block, no explanation.\n\n${F}javascript\n${pricingSrc}${F}`,
      score: (text) => { const c = firstCode(text); return runChecks(refactorCtx(c), refactorChecks, c) }, ref: `${F}javascript\n${pricingRef}${F}`, wrong: `${F}javascript\nfunction priceOrder(o){return {}}\nmodule.exports={priceOrder}\n${F}`, subtle: `${F}javascript\n${pricingSubtle}${F}` },
    { id: 'H3-agent', kind: 'agent2',
      system: 'You are a coding agent working in a small Node.js library. Use the tools to inspect and fix the code. Run the tests before and after you change anything. The comments in the source describe the intended behaviour of each function. When you are done, reply with a short final message and make no tool call.',
      user: 'Some tests in this billing library fail, and there are probably more bugs than the failing tests show. Fix the library so that every function behaves exactly as documented in its comments. Do not edit the tests.',
      files: { ...src, 'test/lib.test.js': visibleTest, 'package.json': '{"name":"billing-lib","version":"1.0.0"}' },
      hidden: { 'lib.hidden.test.js': hiddenTest }, ref: fixed, tools: agentTools, maxSteps: 25 },
  ]
  return tasks
}
