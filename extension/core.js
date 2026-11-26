'use strict';

const LavariExtensionCore = (() => {
  const currencies = new Set(['AUD', 'USD', 'NZD', 'GBP', 'EUR', 'CAD', 'SGD', 'CHF', 'HKD', 'ZAR', 'MYR', 'INR', 'CNY']);

  function originFor(value) {
    const url = new URL(value.trim());
    const local = url.hostname === 'localhost' || url.hostname === '127.0.0.1';
    if ((url.protocol !== 'https:' && !(local && url.protocol === 'http:')) ||
        url.username || url.password || url.pathname !== '/' || url.search || url.hash) {
      throw new Error('Enter the Lavari HTTPS origin without a path, credentials or query. HTTP is allowed only for localhost.');
    }
    return url.origin;
  }

  function scaledInteger(value, precision, label, positive = false) {
    const text = value.trim();
    const pattern = new RegExp(`^\\d+(?:\\.\\d{1,${precision}})?$`);
    if (!pattern.test(text)) throw new Error(`${label} must have at most ${precision} decimal places.`);
    const [whole, fraction = ''] = text.split('.');
    const amount = BigInt(whole) * (10n ** BigInt(precision)) + BigInt(fraction.padEnd(precision, '0') || '0');
    if ((positive && amount === 0n) || amount > BigInt(Number.MAX_SAFE_INTEGER)) {
      throw new Error(`${label} is outside the supported range.`);
    }
    return Number(amount);
  }

  function parseAttributes(text) {
    const seen = new Set();
    return text.split('\n').filter(line => line.trim()).map(line => {
      const parts = line.split('|').map(part => part.trim());
      if (parts.length < 2 || parts.length > 3 || !parts[0] || !parts[1]) {
        throw new Error('Each attribute needs a label and value separated by |.');
      }
      const key = parts[0].toLowerCase().replace(/\s+/g, '-');
      if (seen.has(key)) throw new Error('Attribute labels must be unique.');
      seen.add(key);
      return { key, label: parts[0], value: parts[1], unit: parts[2] || null };
    });
  }

  function sourceUrl(value) {
    if (!value.trim()) return null;
    const url = new URL(value.trim());
    if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) {
      throw new Error('The source must be an HTTP(S) URL without credentials.');
    }
    return url.href;
  }

  function toAmount(cents) {
    if (!Number.isSafeInteger(cents) || cents < 0) throw new Error('The suggested amount is invalid.');
    const value = BigInt(cents);
    return `${value / 100n}.${String(value % 100n).padStart(2, '0')}`;
  }

  return { currencies, originFor, scaledInteger, parseAttributes, sourceUrl, toAmount };
})();

if (typeof module !== 'undefined') module.exports = LavariExtensionCore;
