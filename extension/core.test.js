'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const core = require('./core.js');

test('only accepts secure Lavari origins and local development HTTP', () => {
  assert.equal(core.originFor('https://lavari.example:8443'), 'https://lavari.example:8443');
  assert.equal(core.originFor('http://localhost:5180'), 'http://localhost:5180');
  for (const value of ['http://example.test', 'https://a:b@example.test', 'https://example.test/path',
    'https://example.test/?token=secret', 'file:///test']) {
    assert.throws(() => core.originFor(value));
  }
});

test('monetary and quantity values use exact bounded integer parsing', () => {
  assert.equal(core.scaledInteger('12.05', 2, 'Cost'), 1205);
  assert.equal(core.scaledInteger('1.125', 3, 'Quantity', true), 1125);
  assert.equal(core.toAmount(9007199254740991), '90071992547409.91');
  for (const value of ['1.001', '-1', '1e3', '90071992547409.92']) {
    assert.throws(() => core.scaledInteger(value, 2, 'Cost'));
  }
  assert.throws(() => core.scaledInteger('0', 3, 'Quantity', true));
});

test('attributes reject missing and duplicate labels and preserve units', () => {
  assert.deepEqual(core.parseAttributes('Color | Black\nSize | 10 | cm'), [
    { key: 'color', label: 'Color', value: 'Black', unit: null },
    { key: 'size', label: 'Size', value: '10', unit: 'cm' }
  ]);
  assert.throws(() => core.parseAttributes('Color | Black\ncolor | White'));
  assert.throws(() => core.parseAttributes('Material'));
});

test('source URLs must not contain credentials or unsafe schemes', () => {
  assert.equal(core.sourceUrl('https://example.test/item'), 'https://example.test/item');
  assert.throws(() => core.sourceUrl('https://user:pass@example.test/item'));
  assert.throws(() => core.sourceUrl('javascript:alert(1)'));
});
