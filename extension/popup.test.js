'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const core = require('./core.js');
const config = { apiOrigin: 'https://lavari.com.au' };

class Element {
  constructor() {
    this.value = '';
    this.checked = false;
    this.hidden = false;
    this.disabled = false;
    this.textContent = '';
    this.dataset = {};
    this.style = { setProperty() {}, removeProperty() {} };
    this.listeners = {};
  }
  addEventListener(name, callback) { this.listeners[name] = callback; }
  replaceChildren() {}
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
      source: 'https://supplier.example/item',
      'page-text': 'Product details',
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
  const permissions = {
    clients: 1, jobs: 2, job_line_items: 2, supplier_capture: 2, llm_administration: 1
  };
  const chrome = {
    storage: { session: { get: async () => ({ draft: saved }), set: async () => {} } },
    permissions: { contains: async () => true },
    tabs: { query: async () => [{ id: 1, url: 'https://lavari.com.au/' }] },
    scripting: {
      executeScript: async ({ func, args }) => {
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
                    attributes: [{ label: 'Finish', value: 'Brushed brass' }],
                    identifiers: [{ label: 'SKU', value: 'ABC-123' }],
                    imageUrls: ['https://supplier.example/image.jpg'],
                    requiresUserReview: true
                  })
                }
              }
            }];
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
  const context = {
    chrome,
    document: {
      getElementById: getElement,
      documentElement: { style: { setProperty() {} }, dataset: {}, removeAttribute() {} }
    },
    Option: class { constructor(text, value) { this.text = text; this.value = value; } },
    URL,
    CSS: { supports: () => true },
    matchMedia: () => ({ matches: false, addEventListener() {} }),
    crypto: require('node:crypto'),
    confirm: () => true,
    setTimeout,
    clearTimeout,
    LavariExtensionCore: core,
    LavariExtensionConfig: config
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, 'popup.js'), 'utf8'), context);
  await new Promise(resolve => setImmediate(resolve));

  const useGemini = getElement('use-gemini');
  useGemini.checked = true;
  useGemini.listeners.change();
  assert.equal(getElement('prompt').disabled, false);
  assert.equal(getElement('analyze').disabled, true);

  getElement('consent').checked = true;
  getElement('provider-consent').checked = true;
  getElement('consent').listeners.change();
  await getElement('analyze').listeners.click();

  assert.equal(captureRequest.useGemini, true);
  assert.equal(captureRequest.prompt, 'Extract details');
  assert.equal(captureRequest.userConfirmedConsent, true);
  assert.equal(captureRequest.userConfirmedProviderConsent, true);
  assert.equal(getElement('title').value, 'Manually entered title');
  assert.equal(getElement('description').value, 'Suggested description');
  assert.equal(getElement('attributes').value, 'Finish | Brushed brass');
  assert.equal(getElement('rrp').value, '123.45');
  assert.match(getElement('notes').value, /SKU: ABC-123/);
  assert.match(getElement('notes').value, /Image reference: https:\/\/supplier\.example\/image\.jpg/);
});
