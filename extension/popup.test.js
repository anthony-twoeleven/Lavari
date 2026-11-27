'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const core = require('./core.js');
const config = { apiOrigin: 'https://lavari.com.au' };

class Element {
  constructor(tagName = 'div') {
    this.tagName = tagName.toUpperCase();
    this.value = '';
    this.checked = false;
    this.hidden = false;
    this.disabled = false;
    this.textContent = '';
    this.dataset = {};
    this.style = { setProperty() {}, removeProperty() {} };
    this.listeners = {};
    this.children = [];
  }
  addEventListener(name, callback) { this.listeners[name] = callback; }
  append(child) { this.children.push(child); }
  replaceChildren(...children) { this.children = children; }
  add() {}
  checkValidity() { return true; }
  removeAttribute() {}
}

test('Gemini capture requires provider consent, sends the prompt, and fills review fields', async () => {
  const elements = new Map();
  const getElement = id => {
    if (!elements.has(id)) elements.set(id, new Element());
    return elements.get(id);
  };
  const saved = {
    session: 'extension-test',
    clients: [{ id: 'client-id', name: 'Client' }],
    jobs: [{ id: 'job-id', name: 'Job', currency: 'AUD' }],
    clientId: 'client-id',
    jobId: 'job-id',
    values: {
      source: '',
      'page-text': '',
      prompt: 'Extract details',
      title: 'Manually entered title',
      description: '',
      attributes: '',
      currency: 'AUD',
      rrp: '0.00',
      notes: ''
    }
  };
  let captureRequest;
  let lineItemRequest;
  let persistedDraft;
  const permissions = {
    clients: 1, jobs: 2, job_line_items: 2, supplier_capture: 2, llm_administration: 1
  };
  const chrome = {
    storage: { session: { get: async () => ({ draft: saved }), set: async value => { persistedDraft = value.draft; } } },
    permissions: { contains: async () => true },
    tabs: {
      query: async query => query?.active
        ? [{ id: 2, url: 'https://supplier.example/item?variant=large&session_token=private' }]
        : [{ id: 1, url: 'https://lavari.com.au/' }]
    },
    scripting: {
      executeScript: async ({ func, args }) => {
        if (func.name === 'captureSupplierPage') {
          return [{ result: func(...args) }];
        }
        if (func.name === 'callOnApp') {
          const [, apiPath, method, body] = args;
          if (apiPath === '/api/auth/me') {
            return [{ result: { status: 200, body: { displayName: 'Test User', effectivePermissions: permissions } } }];
          }
          if (apiPath === '/api/integrations/status') {
            return [{ result: { status: 200, body: { geminiExtractionReady: true, statusSummary: 'Gemini ready.' } } }];
          }
          if (apiPath === '/api/account/appearance') {
            return [{ result: { status: 200, body: { mode: 'light', color: 'blue' } } }];
          }
          if (apiPath === '/api/integrations/supplier-capture' && method === 'POST') {
            captureRequest = body;
            return [{
              result: {
                status: 200,
                body: {
                  providerName: 'Gemini',
                  extractedJson: JSON.stringify({
                    name: 'Suggested product',
                    description: 'Suggested description',
                    candidateRrpCents: 12345,
                    currency: 'AUD',
                    sku: 'ABC-123',
                    attributes: [{ key: 'finish', label: 'Finish', value: 'Brushed brass', valueType: 'text', unit: null }],
                    identifiers: [{ key: 'material', label: 'Material', value: 'Brass', valueType: 'text', unit: null }],
                    customFields: [
                      { key: 'selectedsize', label: 'Selected size', value: '500x760', valueType: 'text', unit: 'mm' },
                      { key: 'selectedframecolor', label: 'Selected frame color', value: 'black', valueType: 'text', unit: null },
                      { key: 'framewidth', label: 'Frame width', value: '500', valueType: 'number', unit: 'mm' }
                    ],
                    imageUrls: ['https://supplier.example/image.jpg'],
                    requiresUserReview: true
                  })
                }
              }
            }];
          }
          if (apiPath === '/api/locks/modify') {
            return [{ result: { status: 200, body: { isHeldByCurrentSession: true, leaseId: 'lease-id', fencingVersion: 1 } } }];
          }
          if (apiPath === '/api/jobs/job-id/line-items' && method === 'POST') {
            lineItemRequest = body;
            return [{ result: { status: 201, body: {} } }];
          }
          if (apiPath === '/api/locks/release') {
            return [{ result: { status: 200, body: {} } }];
          }
          throw new Error(`Unexpected API request: ${apiPath}`);
        }
        return [{
          result: {
            mode: 'light',
            values: {
              surface: '#fff', 'on-surface': '#111', primary: '#2255aa',
              'on-primary': '#fff', outline: '#777', 'surface-container': '#eee'
            }
          }
        }];
      }
    }
  };
  const text = value => ({ nodeType: 3, nodeValue: value });
  const element = (tagName, childNodes = [], attributes = {}) => ({
    nodeType: 1,
    tagName: tagName.toUpperCase(),
    childNodes,
    hidden: false,
    hasAttribute: name => Object.hasOwn(attributes, name),
    getAttribute: name => attributes[name] ?? null
  });
  const context = {
    chrome,
    document: {
      getElementById: getElement,
      createElement: tagName => new Element(tagName),
      documentElement: { style: { setProperty() {} }, dataset: {}, removeAttribute() {} }
    },
    location: { href: 'https://supplier.example/item?variant=large&session_token=private#details' },
    getComputedStyle: () => ({ display: 'block', visibility: 'visible', opacity: '1' }),
    Option: class { constructor(text, value) { this.text = text; this.value = value; } },
    URL,
    CSS: { supports: () => true },
    matchMedia: () => ({ matches: false, addEventListener() {} }),
    crypto: require('node:crypto'),
    confirm: () => true,
    navigator: { locks: { request: async (_name, _options, callback) => callback({}) } },
    setTimeout,
    clearTimeout,
    LavariExtensionCore: core,
    LavariExtensionConfig: config
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, 'popup.js'), 'utf8'), context);
  await new Promise(resolve => setImmediate(resolve));
  context.document.body = element('body', [
    element('main', [
      element('h1', [text('Magnus Console Table')]),
      element('p', [text('RRP AUD $2,275.00')]),
      element('header', [text('Private account name')]),
      element('script', [text('private script content')]),
      element('input', [], { type: 'password', value: 'private password' }),
      element('div', [text('Hidden account data')], { hidden: '' }),
      element('div', [text('Navigation account data')], { role: 'navigation' })
    ])
  ]);

  const useGemini = getElement('use-gemini');
  useGemini.checked = true;
  useGemini.listeners.change();
  assert.equal(getElement('prompt').disabled, false);
  assert.equal(getElement('analyze').disabled, true);

  await getElement('capture').listeners.click();
  assert.equal(getElement('source').value, 'https://supplier.example/item?variant=large');
  assert.match(getElement('page-text').value, /<h1>Magnus Console Table\s*<\/h1>/);
  assert.match(getElement('page-text').value, /RRP AUD \$2,275\.00/);
  assert.doesNotMatch(getElement('page-text').value,
    /Private account name|private script content|private password|Hidden account data|Navigation account data/);
  assert.equal(getElement('consent').checked, false);

  getElement('consent').checked = true;
  getElement('provider-consent').checked = true;
  getElement('consent').listeners.change();
  await getElement('analyze').listeners.click();

  assert.equal(captureRequest.useGemini, true);
  assert.equal(captureRequest.sourceUrl, 'https://supplier.example/item?variant=large');
  assert.match(captureRequest.pageTextContent, /Magnus Console Table/);
  assert.equal(captureRequest.prompt, 'Extract details');
  assert.equal(captureRequest.userConfirmedConsent, true);
  assert.equal(captureRequest.userConfirmedProviderConsent, true);
  assert.equal(getElement('title').value, 'Manually entered title');
  assert.equal(getElement('description').value, 'Suggested description');
  const rows = getElement('attributes').children;
  assert.equal(rows.length, 6);
  const getRowField = (row, label) => row.children.find(child =>
    child.tagName === 'LABEL' && child.textContent === label)?.children[0];
  const selectedSize = rows.find(row => getRowField(row, 'Attribute label')?.value === 'Selected size');
  assert.equal(getRowField(selectedSize, 'Value').value, '500x760');
  assert.equal(getRowField(selectedSize, 'Unit (optional)').value, 'mm');
  assert.equal(getRowField(selectedSize, 'Value type').value, 'text');
  const frameWidth = rows.find(row => getRowField(row, 'Attribute label')?.value === 'Frame width');
  assert.equal(getRowField(frameWidth, 'Value').type, 'number');
  assert.equal(getRowField(frameWidth, 'Value type').value, 'number');
  assert.equal(getElement('rrp').value, '123.45');
  assert.equal(getElement('notes').value.includes('SKU: ABC-123'), false);
  assert.match(getElement('notes').value, /Image reference: https:\/\/supplier\.example\/image\.jpg/);
  assert.ok(persistedDraft.values.attributes.some(attribute => attribute.key === 'selectedsize'));

  getElement('line-form').listeners.submit({ preventDefault() {} });
  await new Promise(resolve => setImmediate(resolve));
  assert.ok(lineItemRequest.selectedAttributes.some(attribute =>
    attribute.key === 'selectedsize' && attribute.value === '500x760' && attribute.unit === 'mm'));
  assert.ok(lineItemRequest.selectedAttributes.some(attribute =>
    attribute.key === 'sku' && attribute.value === 'ABC-123'));
  assert.ok(lineItemRequest.selectedAttributes.some(attribute =>
    attribute.key === 'framewidth' && attribute.valueType === 'number' && attribute.value === '500'));
});
