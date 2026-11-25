// Lavari browser-side API fetch adapter, timezone detection, file download & dirty-navigation guard.
window.lavariInterop = {
  csrfToken: null,
  editingSessionId: null,

  setCsrfToken(token) {
    this.csrfToken = token || null;
  },

  setEditingSession(sessionId) {
    this.editingSessionId = sessionId || null;
  },

  getBrowserTimeZone() {
    try {
      return Intl.DateTimeFormat().resolvedOptions().timeZone || null;
    } catch {
      return null;
    }
  },

  async fetchJson(method, url, bodyJson) {
    const headers = { 'Accept': 'application/json' };
    if (bodyJson !== null && bodyJson !== undefined) {
      headers['Content-Type'] = 'application/json';
    }
    if (this.csrfToken) {
      headers['X-CSRF-TOKEN'] = this.csrfToken;
    }
    if (this.editingSessionId) {
      headers['X-Editing-Session'] = this.editingSessionId;
    }

    const resp = await fetch(url, {
      method,
      credentials: 'same-origin',
      headers,
      body: bodyJson !== null && bodyJson !== undefined ? bodyJson : undefined
    });

    const text = await resp.text();
    return {
      status: resp.status,
      ok: resp.ok,
      body: text
    };
  },

  async uploadFile(url, fields, fileName, contentType, base64Bytes) {
    const bin = atob(base64Bytes);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) {
      bytes[i] = bin.charCodeAt(i);
    }
    const blob = new Blob([bytes], { type: contentType || 'application/octet-stream' });
    const form = new FormData();
    form.append('file', blob, fileName || 'upload.jpg');
    if (fields) {
      for (const [k, v] of Object.entries(fields)) {
        if (v !== null && v !== undefined) {
          form.append(k, String(v));
        }
      }
    }

    const headers = {};
    if (this.csrfToken) headers['X-CSRF-TOKEN'] = this.csrfToken;
    if (this.editingSessionId) headers['X-Editing-Session'] = this.editingSessionId;

    const resp = await fetch(url, {
      method: 'POST',
      credentials: 'same-origin',
      headers,
      body: form
    });
    const text = await resp.text();
    return { status: resp.status, ok: resp.ok, body: text };
  },

  downloadUrl(url) {
    const a = document.createElement('a');
    a.href = url;
    a.rel = 'noopener';
    document.body.appendChild(a);
    a.click();
    a.remove();
  },

  setDirtyGuard(isDirty) {
    if (isDirty) {
      window.onbeforeunload = (e) => {
        e.preventDefault();
        e.returnValue = '';
      };
    } else {
      window.onbeforeunload = null;
    }
  }
};
