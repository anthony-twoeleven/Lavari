'use strict';

const $ = id => document.getElementById(id);
const API_ORIGIN = LavariExtensionCore.originFor(LavariExtensionConfig.apiOrigin);
const fields = ['client-search', 'job-search', 'page-text', 'prompt', 'title', 'source',
  'description', 'room', 'attributes', 'requirements', 'quantity', 'unit', 'currency', 'cost', 'rrp', 'sell', 'notes'];
const defaults = { quantity: '1', unit: 'ea', currency: 'AUD', cost: '0.00', rrp: '0.00', sell: '0.00' };
let state = { session: '', clients: [], jobs: [], clientId: '', jobId: '', pendingSave: false };
let session = null;
let integrationStatus = null;
let busy = false;
let initialized = false;
let saveChain = Promise.resolve();
let attributes = [];

function attributeKey(label) {
  const key = label.trim().toLowerCase().replace(/[^a-z0-9_-]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 80).replace(/-+$/g, '');
  return key || 'attribute';
}

function attributeValues() {
  return attributes.map(attribute => ({ ...attribute }));
}

function setAttributes(values) {
  const container = $('attributes');
  container.replaceChildren();
  attributes = Array.isArray(values) ? values.slice(0, 25).map((value, index) => {
    const label = typeof value?.label === 'string' ? value.label.slice(0, 100) : '';
    return {
      key: typeof value?.key === 'string' && /^[a-z0-9][a-z0-9_-]{0,79}$/i.test(value.key)
        ? value.key : attributeKey(label || `attribute-${index + 1}`),
      label,
      value: typeof value?.value === 'string' ? value.value.slice(0, 500) : '',
      valueType: ['text', 'number', 'url', 'date'].includes(value?.valueType) ? value.valueType : 'text',
      unit: typeof value?.unit === 'string' ? value.unit.slice(0, 40) : ''
    };
  }) : [];

  for (const attribute of attributes) {
    const row = document.createElement('div');
    row.className = 'attribute-row';
    const makeInput = (labelText, property, type = 'text') => {
      const label = document.createElement('label');
      label.textContent = labelText;
      const input = document.createElement('input');
      input.type = type;
      input.value = attribute[property];
      input.maxLength = property === 'value' ? 500 : property === 'unit' ? 40 : 100;
      input.addEventListener('input', () => {
        attribute[property] = input.value;
        if (property === 'label') attribute.key = attributeKey(input.value);
      });
      label.append(input);
      row.append(label);
      return input;
    };

    makeInput('Attribute label', 'label');
    const valueInput = makeInput('Value', 'value');
    valueInput.type = attribute.valueType === 'number' ? 'number' :
      attribute.valueType === 'url' ? 'url' : attribute.valueType === 'date' ? 'date' : 'text';
    if (valueInput.type === 'number') valueInput.step = 'any';
    const typeLabel = document.createElement('label');
    typeLabel.textContent = 'Value type';
    const typeSelect = document.createElement('select');
    for (const [value, text] of [['text', 'Text'], ['number', 'Number'], ['url', 'URL'], ['date', 'Date']]) {
      const option = document.createElement('option');
      option.value = value;
      option.textContent = text;
      typeSelect.append(option);
    }
    typeSelect.value = attribute.valueType;
    typeSelect.addEventListener('change', () => {
      attribute.valueType = typeSelect.value;
      valueInput.type = attribute.valueType === 'number' ? 'number' :
        attribute.valueType === 'url' ? 'url' : attribute.valueType === 'date' ? 'date' : 'text';
    });
    typeLabel.append(typeSelect);
    row.append(typeLabel);
    makeInput('Unit (optional)', 'unit');
    const remove = document.createElement('button');
    remove.type = 'button';
    remove.className = 'secondary';
    remove.textContent = 'Remove';
    remove.addEventListener('click', () => {
      attributes = attributes.filter(item => item !== attribute);
      setAttributes(attributes);
      updateButtons();
      persist().catch(() => message('The draft could not be preserved in this browser session.', true));
    });
    row.append(remove);
    container.append(row);
  }
}

function setFieldValue(id, value) {
  if (id === 'attributes') {
    if (Array.isArray(value)) {
      setAttributes(value);
    } else if (typeof value === 'string' && value.trim()) {
      setAttributes(LavariExtensionCore.parseAttributes(value).map(attribute => ({
        ...attribute, valueType: 'text'
      })));
    } else {
      setAttributes([]);
    }
  }
  else $(id).value = value ?? '';
}

function fieldValue(id) {
  return id === 'attributes' ? attributeValues() : $(id).value;
}

function lineItemAttributes() {
  const seen = new Set();
  return attributes.filter(attribute => attribute.label.trim() || attribute.value.trim()).map(attribute => {
    const label = attribute.label.trim();
    const key = /^[a-z0-9][a-z0-9_-]{0,79}$/i.test(attribute.key)
      ? attribute.key : attributeKey(label);
    const value = attribute.value.trim();
    if (!label || !value || seen.has(key)) throw new Error('Each attribute needs a unique label and value.');
    seen.add(key);
    return { key, label, value, valueType: attribute.valueType, unit: attribute.unit.trim() || null };
  });
}

function message(text, isError = false) {
  $('error').hidden = !isError;
  $('error').textContent = isError ? text : '';
  $('status').textContent = isError ? '' : text;
}

function can(module, level) {
  return session && (session.isFullAccess || (session.effectivePermissions?.[module] ?? 0) >= level);
}

function renderSelect(id, items, selected, placeholder) {
  const element = $(id);
  element.replaceChildren(new Option(placeholder, ''));
  for (const item of items) element.add(new Option(item.name, item.id));
  element.value = selected;
}

function updateButtons() {
  const authenticated = Boolean(session);
  $('login-gate').hidden = authenticated;
  $('workspace').hidden = !authenticated;
  $('open-window').hidden = !authenticated;
  $('connect').disabled = busy || !initialized;
  const allowed = can('clients', 1) && can('jobs', 2) && can('job_line_items', 2);
  const canExtract = Boolean(integrationStatus?.geminiExtractionReady && can('llm_administration', 1));
  $('use-gemini').disabled = busy || !canExtract;
  $('provider-consent-label').hidden = !$('use-gemini').checked;
  $('prompt').disabled = busy || !canExtract || !$('use-gemini').checked;
  $('save').disabled = busy || !initialized || !allowed || !state.clientId || !state.jobId ||
    !$('title').value.trim() || !$('line-form').checkValidity() || state.pendingSave;
  $('analyze').disabled = busy || !can('supplier_capture', 2) || !state.jobId ||
    !$('source').value.trim() || !$('consent').checked ||
    ($('use-gemini').checked && (!canExtract || !$('provider-consent').checked));
  $('uncertain').hidden = !state.pendingSave;
  for (const id of ['connect', 'find-clients', 'find-jobs', 'capture', 'discard']) $(id).disabled = busy || !initialized;
}

function snapshot() {
  return { ...state, values: Object.fromEntries(fields.map(id => [id, fieldValue(id)])) };
}

function persist() {
  const value = snapshot();
  saveChain = saveChain.then(
    () => chrome.storage.session.set({ draft: value }),
    () => chrome.storage.session.set({ draft: value }));
  return saveChain;
}

async function setBusy(value) {
  busy = value;
  updateButtons();
}

class ApiFailure extends Error {
  constructor(status, text) { super(text); this.status = status; }
}

// Runs in the browser's isolated world on the already signed-in Lavari tab.
async function callOnApp(expectedOrigin, path, method, body, editingSession) {
  if (location.origin !== expectedOrigin || !path.startsWith('/api/') || new URL(path, location.origin).origin !== expectedOrigin) {
    throw new Error('The Lavari tab changed. Reconnect and try again.');
  }
  const headers = { Accept: 'application/json', 'X-Editing-Session': editingSession };
  if (method !== 'GET') {
    const csrfResponse = await fetch('/api/auth/antiforgery', { credentials: 'same-origin', redirect: 'error' });
    if (!csrfResponse.ok) return { status: csrfResponse.status, body: null };
    const csrf = await csrfResponse.json();
    headers['X-CSRF-TOKEN'] = csrf.token;
    headers['Content-Type'] = 'application/json';
  }
  const response = await fetch(path, {
    method, headers, credentials: 'same-origin', redirect: 'error',
    body: method === 'GET' ? undefined : JSON.stringify(body)
  });
  const text = await response.text();
  let result = null;
  if (text) {
    try { result = JSON.parse(text); }
    catch { return { status: response.status, malformed: true }; }
  }
  return { status: response.status, body: result };
}

async function appTab() {
  const tabs = await chrome.tabs.query({});
  const tab = tabs.find(item => {
    if (!item.url || !item.id) return false;
    try { return new URL(item.url).origin === API_ORIGIN; } catch { return false; }
  });
  if (!tab) throw new Error('Open Lavari in this browser profile and sign in before continuing.');
  return tab.id;
}

async function api(path, method = 'GET', body = null) {
  const tabId = await appTab();
  const results = await chrome.scripting.executeScript({
    target: { tabId }, func: callOnApp, args: [API_ORIGIN, path, method, body, state.session]
  });
  const result = results[0]?.result;
  if (!result) throw new Error('Could not contact the Lavari tab. Reconnect and try again.');
  if (result.status < 200 || result.status >= 300) {
    if (result.status === 401) {
      session = null;
      $('identity').textContent = '';
      updateButtons();
    }
    const error = result.body;
    const detail = error?.message ? `${error.message}${error.possibleCause ? ` ${error.possibleCause}` : ''}${error.correlationId ? ` Reference: ${error.correlationId}` : ''}` :
      result.status === 401 ? 'Your Lavari session has expired. Sign in again.' : `Lavari rejected the request (${result.status}).`;
    throw new ApiFailure(result.status, detail);
  }
  if (result.malformed) throw new Error('Lavari returned an unreadable response. Retry or contact support.');
  return result.body;
}

async function readTheme(expectedOrigin, appearance) {
  if (location.origin !== expectedOrigin) throw new Error('The Lavari tab changed.');
  const root = document.documentElement;
  const mode = appearance.mode === 'system' ? (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light') : appearance.mode;
  if (root.dataset.theme !== mode || root.dataset.color !== appearance.color) return null;
  const style = getComputedStyle(root);
  const names = ['surface', 'on-surface', 'primary', 'on-primary', 'outline', 'surface-container'];
  return { mode, values: Object.fromEntries(names.map(name => [name, style.getPropertyValue(`--md-sys-color-${name}`).trim()])) };
}

async function applyTheme() {
  try {
    const appearance = await api('/api/account/appearance');
    const result = await chrome.scripting.executeScript({
      target: { tabId: await appTab() }, func: readTheme, args: [API_ORIGIN, appearance]
    });
    const theme = result[0]?.result;
    if (!theme) throw new Error('Open the saved appearance in Lavari to use its colors.');
    const keys = { surface: 'surface', 'on-surface': 'on-surface', primary: 'primary',
      'on-primary': 'on-primary', outline: 'outline', 'surface-container': 'container' };
    for (const [source, target] of Object.entries(keys)) {
      const color = theme.values[source];
      if (!color || !CSS.supports('color', color) || /(?:url|var)\s*\(/i.test(color)) throw new Error('Lavari returned an unsupported theme color.');
      document.documentElement.style.setProperty(`--${target}`, color);
    }
    document.documentElement.dataset.theme = theme.mode;
    document.documentElement.style.colorScheme = theme.mode;
    return true;
  } catch (error) {
    document.documentElement.removeAttribute('style');
    delete document.documentElement.dataset.theme;
    message(`Using the system theme: ${error.message}`);
    return false;
  }
}

async function refreshSession() {
  session = await api('/api/auth/me');
  $('identity').textContent = `Signed in as ${session.displayName}.`;
  integrationStatus = await api('/api/integrations/status');
  $('provider-status').textContent = integrationStatus.statusSummary;
  updateButtons();
  return applyTheme();
}

async function openAppForSignIn() {
  const tabs = await chrome.tabs.query({});
  const tab = tabs.find(item => {
    if (!item.url || !item.id) return false;
    try { return new URL(item.url).origin === API_ORIGIN; } catch { return false; }
  });
  if (tab?.id) {
    await chrome.tabs.update(tab.id, { active: true });
  } else {
    await chrome.tabs.create({ url: `${API_ORIGIN}/` });
  }
}

async function searchClients() {
  const data = await api(`/api/clients?page=1&pageSize=50&search=${encodeURIComponent($('client-search').value)}`);
  state.clients = data.items.map(item => ({ id: item.id, name: item.name }));
  state.clientId = '';
  state.jobId = '';
  state.jobs = [];
  renderSelect('client', state.clients, '', 'Select a client');
  renderSelect('job', [], '', 'Select a job');
  message(`${data.totalCount} matching clients. Select a client.`);
}

async function searchJobs() {
  if (!state.clientId) throw new Error('Select a client first.');
  const data = await api(`/api/jobs?clientId=${encodeURIComponent(state.clientId)}&page=1&pageSize=50&search=${encodeURIComponent($('job-search').value)}`);
  state.jobs = data.items.map(item => ({ id: item.id, name: item.name, currency: item.defaultCurrency }));
  state.jobId = '';
  renderSelect('job', state.jobs, '', 'Select a job');
  message(`${data.totalCount} matching jobs. Select a job.`);
}

function captureSupplierPage(expectedOrigin) {
  const maxLength = 16000;
  const maxNodes = 20000;
  const maxDepth = 64;
  const excluded = new Set([
    'script', 'style', 'noscript', 'svg', 'iframe', 'object', 'embed', 'template',
    'form', 'input', 'select', 'textarea', 'button', 'option', 'nav', 'header',
    'footer', 'aside', 'dialog', 'meta', 'link', 'canvas'
  ]);
  const excludedRoles = new Set(['navigation', 'banner', 'contentinfo', 'complementary', 'dialog', 'search']);
  const allowed = new Set([
    'main', 'article', 'section', 'div', 'p', 'br', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
    'span', 'strong', 'b', 'em', 'i', 'small', 'ul', 'ol', 'li', 'dl', 'dt', 'dd',
    'table', 'thead', 'tbody', 'tfoot', 'tr', 'th', 'td', 'img'
  ]);
  const block = new Set([
    'main', 'article', 'section', 'div', 'p', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
    'ul', 'ol', 'li', 'dl', 'dt', 'dd', 'table', 'tr', 'br'
  ]);
  const cleanUrl = value => {
    let url;
    try { url = new URL(value, location.href); } catch { return null; }
    if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) return null;
    url.hash = '';
    for (const key of [...url.searchParams.keys()]) {
      const normalized = key.toLowerCase().replace(/[-_.]/g, '');
      if (['key', 'apikey', 'code'].includes(normalized) ||
          ['token', 'auth', 'signature', 'secret', 'password', 'session', 'cookie', 'credential']
            .some(fragment => normalized.includes(fragment))) {
        url.searchParams.delete(key);
      }
    }
    return url.href;
  };
  const sourceUrl = cleanUrl(location.href);
  if (!sourceUrl) throw new Error('Capture is available only on public HTTP(S) supplier pages.');
  if (new URL(sourceUrl).origin === expectedOrigin) {
    throw new Error('Capture a supplier page, not a Lavari page.');
  }
  const parts = [];
  let length = 0;
  let visited = 0;
  const append = value => {
    if (length >= maxLength) return false;
    const remaining = maxLength - length;
    parts.push(value.slice(0, remaining));
    length += Math.min(value.length, remaining);
    return value.length <= remaining;
  };
  const escapeText = value => value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  const escapeAttribute = value => escapeText(value).replace(/"/g, '&quot;');
  const visit = (node, depth) => {
    if (length >= maxLength || visited++ >= maxNodes || depth > maxDepth) return;
    if (node.nodeType === 3) {
      const text = node.nodeValue?.replace(/\s+/g, ' ').trim();
      if (text) append(escapeText(text) + ' ');
      return;
    }
    if (node.nodeType !== 1) return;
    const tag = node.tagName.toLowerCase();
    if (excluded.has(tag) || node.hidden || node.hasAttribute('hidden') ||
        node.hasAttribute('inert') || node.getAttribute('aria-hidden') === 'true' ||
        excludedRoles.has(node.getAttribute('role')) ||
        node.hasAttribute('contenteditable') || node.getAttribute('type')?.toLowerCase() === 'password') return;
    const style = getComputedStyle(node);
    if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse' ||
        Number(style.opacity) === 0) return;

    if (tag === 'img') {
      const src = cleanUrl(node.getAttribute('src') || '');
      if (src) {
        const alt = node.getAttribute('alt') || '';
        append(`<img src="${escapeAttribute(src)}"${alt ? ` alt="${escapeAttribute(alt)}"` : ''}>`);
      }
      return;
    }

    const outputTag = allowed.has(tag) ? tag : null;
    if (block.has(tag)) append('<br>');
    if (outputTag) append(`<${outputTag}>`);
    for (const child of node.childNodes) {
      if (visited >= maxNodes || length >= maxLength) break;
      visit(child, depth + 1);
    }
    if (outputTag && tag !== 'br') append(`</${outputTag}>`);
  };

  visit(document.body, 0);
  let html = parts.join('').trim();
  if (!html) throw new Error('No visible product content was found on this page.');
  if (html.length === maxLength) {
    const boundary = html.lastIndexOf('>');
    if (boundary > 0) html = html.slice(0, boundary + 1);
  }
  return { url: sourceUrl, html };
}

async function operation(work) {
  if (busy) return;
  await setBusy(true);
  try { await work(); }
  catch (error) { message(error.message || 'The operation failed. Retry or contact support.', true); }
  finally {
    try { await persist(); } catch { message('The draft could not be preserved in this browser session.', true); }
    await setBusy(false);
  }
}

$('connect').addEventListener('click', async () => {
  if (busy) return;
  const parsed = new URL(API_ORIGIN);
  try {
    const granted = await chrome.permissions.request({ origins: [`${parsed.protocol}//${parsed.hostname}/*`] });
    if (!granted) throw new Error('Browser permission for the configured Lavari address is required.');
    await operation(async () => {
      try { await appTab(); }
      catch {
        await openAppForSignIn();
        message('Sign in on the Lavari page, then return here and continue.');
        return;
      }
      try {
        const themed = await refreshSession();
        if (themed) message(`Signed in as ${session.displayName}.`);
      } catch (error) {
        if (!(error instanceof ApiFailure) || error.status !== 401) throw error;
        await openAppForSignIn();
        message('Sign in on the Lavari page, then return here and continue.');
      }
    });
  } catch (error) {
    message(error.message || 'Could not connect to Lavari.', true);
  }
});

$('find-clients').addEventListener('click', () => operation(searchClients));
$('find-jobs').addEventListener('click', () => operation(searchJobs));
$('client').addEventListener('change', () => operation(async () => {
  state.clientId = $('client').value; state.jobId = ''; state.jobs = [];
  renderSelect('job', [], '', 'Select a job');
  if (state.clientId) await searchJobs();
}));
$('job').addEventListener('change', () => {
  state.jobId = $('job').value;
  const job = state.jobs.find(item => item.id === state.jobId);
  if (job?.currency) $('currency').value = job.currency;
  updateButtons();
  persist().catch(() => message('The draft could not be preserved in this browser session.', true));
});

$('capture').addEventListener('click', () => operation(async () => {
  const tabs = await chrome.tabs.query({ active: true, currentWindow: true });
  const tab = tabs[0];
  if (!tab?.id) throw new Error('Open a supplier product page before capturing.');
  const result = await chrome.scripting.executeScript({
    target: { tabId: tab.id },
    func: captureSupplierPage,
    args: [API_ORIGIN]
  });
  const captured = result[0]?.result;
  if (!captured?.html || captured.html.length > 16000) {
    throw new Error('The visible product content could not be captured within the supported size.');
  }
  $('source').value = captured.url;
  $('page-text').value = captured.html;
  $('consent').checked = false;
  $('provider-consent').checked = false;
  message('Review the captured page HTML and source URL before sending.');
}));

function applySuggestion(draft) {
  let extracted;
  try { extracted = JSON.parse(draft.extractedJson); }
  catch { throw new Error('The returned draft could not be read. Enter the line item manually.'); }
  if (!$('title').value.trim() && typeof extracted.name === 'string') $('title').value = extracted.name;
  if (!$('description').value.trim() && typeof extracted.description === 'string') {
    $('description').value = extracted.description;
  }
  const suggestedAttributes = [
    ...(Array.isArray(extracted.attributes) ? extracted.attributes : []),
    ...(Array.isArray(extracted.identifiers) ? extracted.identifiers : []),
    ...(Array.isArray(extracted.customFields) ? extracted.customFields : [])
  ];
  if (typeof extracted.sku === 'string' && extracted.sku.trim()) {
    suggestedAttributes.unshift({ key: 'sku', label: 'SKU', value: extracted.sku, valueType: 'text' });
  }
  if (suggestedAttributes.length) {
    const merged = [...attributeValues(), ...suggestedAttributes
      .filter(item => typeof item?.label === 'string' && typeof item?.value === 'string')]
      .reduce((all, item) => {
        const label = item.label.trim();
        const key = typeof item.key === 'string' && /^[a-z0-9][a-z0-9_-]{0,79}$/i.test(item.key)
          ? item.key : attributeKey(label);
        if (label && item.value.trim() && !all.some(saved => saved.key.toLowerCase() === key.toLowerCase())) {
          all.push({
            key, label, value: item.value.trim(),
            valueType: ['text', 'number', 'url', 'date'].includes(item.valueType) ? item.valueType : 'text',
            unit: typeof item.unit === 'string' ? item.unit : ''
          });
        }
        return all;
      }, []);
    setAttributes(merged.slice(0, 25));
  }
  const selectedCurrency = $('currency').value.trim().toUpperCase();
  let currencyWarning = '';
  if (extracted.candidateRrpCents != null && $('rrp').value === '0.00' &&
      typeof extracted.currency === 'string' && extracted.currency.toUpperCase() === selectedCurrency) {
    $('rrp').value = LavariExtensionCore.toAmount(extracted.candidateRrpCents);
  } else if (extracted.candidateRrpCents != null &&
      typeof extracted.currency === 'string' && extracted.currency.toUpperCase() !== selectedCurrency) {
    currencyWarning = ' The suggested price uses a different currency; enter it manually if appropriate.';
  }
  if (!$('notes').value.trim()) {
    const references = [];
    if (Array.isArray(extracted.imageUrls)) {
      references.push(...extracted.imageUrls
        .filter(url => typeof url === 'string')
        .map(url => `Image reference: ${url}`));
    }
    if (references.length) $('notes').value = references.join('\n');
  }
  updateButtons();
  const source = draft.providerName === 'Gemini' ? 'Gemini' : 'Local heuristic';
  message(`${source} suggestions are ready. Verify every field before saving.${currencyWarning}`);
}

$('analyze').addEventListener('click', () => operation(async () => {
  if (!$('consent').checked) throw new Error('Review and confirm the content before sending.');
  const useGemini = $('use-gemini').checked;
  if (useGemini && !$('provider-consent').checked) {
    throw new Error('Confirm that you consent to send the captured page content to Google Gemini.');
  }
  const draft = await api('/api/integrations/supplier-capture', 'POST', {
    clientId: state.clientId, jobId: state.jobId, sourceUrl: LavariExtensionCore.sourceUrl($('source').value),
    pageTextContent: $('page-text').value,
    prompt: useGemini ? ($('prompt').value.trim() || null) : null,
    useGemini,
    userConfirmedConsent: true,
    userConfirmedProviderConsent: useGemini && $('provider-consent').checked
  });
  applySuggestion(draft);
}));

function lineItem() {
  const currency = $('currency').value.trim().toUpperCase();
  if (!LavariExtensionCore.currencies.has(currency)) throw new Error('Choose a supported two-decimal currency.');
  return {
    clientId: state.clientId, title: $('title').value.trim(), sourceUrl: LavariExtensionCore.sourceUrl($('source').value),
    description: $('description').value.trim() || null, roomOrArea: $('room').value.trim() || null,
    selectedAttributes: lineItemAttributes(),
    requirementsNote: $('requirements').value.trim() || null,
    quantityMilli: LavariExtensionCore.scaledInteger($('quantity').value, 3, 'Quantity', true),
    unit: $('unit').value.trim(), currency,
    costCents: LavariExtensionCore.scaledInteger($('cost').value, 2, 'Cost'),
    rrpCents: LavariExtensionCore.scaledInteger($('rrp').value, 2, 'RRP'),
    unitSellPriceCents: LavariExtensionCore.scaledInteger($('sell').value, 2, 'Sell price'),
    notes: $('notes').value.trim() || null
  };
}

$('line-form').addEventListener('submit', event => {
  event.preventDefault();
  if ($('save').disabled) return;
  navigator.locks.request('lavari-line-item-save', { ifAvailable: true }, async lock => {
    if (!lock) { message('Another companion window is already saving.', true); return; }
    await operation(async () => {
    const item = lineItem();
    const action = { recordType: 'Job', recordId: state.jobId };
    const lease = await api('/api/locks/modify', 'POST', action);
    if (!lease.isHeldByCurrentSession || !lease.leaseId || lease.fencingVersion == null) {
      if (lease.isCurrentSessionQueued) await api('/api/locks/cancel-queue', 'POST', action);
      throw new Error('The job is being edited elsewhere. Wait for it to be free and try again.');
    }
    action.leaseId = lease.leaseId; action.fencingVersion = lease.fencingVersion;
    let saved = false;
    try {
      state.pendingSave = true;
      await persist();
      await api(`/api/jobs/${encodeURIComponent(state.jobId)}/line-items`, 'POST', { ...item, ...action });
      saved = true;
      state.pendingSave = false;
      for (const id of fields.slice(2)) setFieldValue(id, defaults[id] || '');
      $('consent').checked = false;
      $('provider-consent').checked = false;
      $('use-gemini').checked = false;
      await persist();
    } catch (error) {
      if (!saved && error instanceof ApiFailure && error.status >= 400 && error.status < 500) state.pendingSave = false;
      if (saved) {
        state.pendingSave = true;
        throw new Error('The line item was saved, but the draft could not be cleared. Check the job before trying again.');
      }
      throw error;
    } finally {
      try { await api('/api/locks/release', 'POST', action); }
      catch (error) {
        if (saved) message(`Line item saved, but the edit lock could not be released: ${error.message}`, true);
        else message(`Could not release the edit lock: ${error.message}`, true);
      }
    }
    if (!$('error').hidden) return;
    message('Line item added to the selected job.');
    });
  }).catch(() => message('Could not coordinate saving across companion windows.', true));
});

$('acknowledge').addEventListener('click', () => {
  state.pendingSave = false;
  updateButtons();
  persist().catch(() => message('The draft could not be preserved in this browser session.', true));
});
$('add-attribute').addEventListener('click', () => {
  if (attributes.length >= 25) return message('A line item can contain at most 25 attributes.', true);
  attributes = [...attributes, { key: 'attribute', label: '', value: '', valueType: 'text', unit: '' }];
  setAttributes(attributes);
  updateButtons();
  persist().catch(() => message('The draft could not be preserved in this browser session.', true));
});
$('discard').addEventListener('click', () => operation(async () => {
  if (!confirm('Discard the current draft?')) return;
  for (const id of fields.slice(2)) setFieldValue(id, defaults[id] || '');
  $('consent').checked = false;
  $('provider-consent').checked = false;
  $('use-gemini').checked = false;
  state.pendingSave = false;
  message('Draft discarded.');
}));
$('open-window').addEventListener('click', () => {
  chrome.windows.create({ url: chrome.runtime.getURL('popup.html'), type: 'popup', width: 440, height: 850 })
    .catch(() => message('Could not open the companion window.', true));
});
for (const id of fields) $(id).addEventListener('input', () => {
  if (id === 'page-text' || id === 'source' || id === 'prompt') {
    $('consent').checked = false;
    $('provider-consent').checked = false;
  }
  updateButtons();
  if (initialized) persist().catch(() => message('The draft could not be preserved in this browser session.', true));
});
$('consent').addEventListener('change', updateButtons);
$('use-gemini').addEventListener('change', () => {
  $('provider-consent').checked = false;
  updateButtons();
});
$('provider-consent').addEventListener('change', updateButtons);
$('attributes').addEventListener('change', () => {
  if (initialized) persist().catch(() => message('The draft could not be preserved in this browser session.', true));
});
matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
  if (session) setTimeout(() => applyTheme(), 150);
});

(async () => {
  try {
    const saved = (await chrome.storage.session.get('draft')).draft;
    if (saved) {
      delete saved.origin;
      state = { ...state, ...saved };
    }
    if (!state.session) state.session = `extension-${crypto.randomUUID()}`;
    for (const id of fields) setFieldValue(id, state.values?.[id] ?? defaults[id] ?? '');
    renderSelect('client', state.clients, state.clientId, 'Select a client');
    renderSelect('job', state.jobs, state.jobId, 'Select a job');
    initialized = true;
    updateButtons();
    const parsed = new URL(API_ORIGIN);
    const granted = await chrome.permissions.contains({
      origins: [`${parsed.protocol}//${parsed.hostname}/*`]
    });
    if (granted) {
      try { await refreshSession(); }
      catch (error) {
        if (!(error instanceof ApiFailure) || error.status !== 401) throw error;
        message('Sign in on the Lavari page, then continue here.');
      }
    }
  } catch (error) {
    initialized = true;
    updateButtons();
    message(error.message || 'Could not restore the browser session.', true);
  }
})();
