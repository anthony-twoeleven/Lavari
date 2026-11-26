// Lavari browser-side API fetch adapter, timezone detection, file download & dirty-navigation guard.
window.lavariInterop = {
  csrfToken: null,
  editingSessionId: null,

  appearanceMode: 'system',
  appearanceColor: 'lavender',
  colorSchemeQuery: window.matchMedia('(prefers-color-scheme: dark)'),
  setAppearance(mode, color) {
    this.appearanceMode = mode;
    this.appearanceColor = color;
    document.documentElement.dataset.theme = mode === 'system'
      ? (this.colorSchemeQuery.matches ? 'dark' : 'light')
      : mode;
    document.documentElement.dataset.color = color;
  },

  openDialog(element) {
    if (element.classList.contains('lv-dialog')) {
      element.oncancel = event => event.preventDefault();
    }
    if (!element.open) element.showModal();
  },

  closeDialog(element) {
    if (element.open) element.close();
  },

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
    return new Blob([JSON.stringify({
      status: resp.status,
      ok: resp.ok,
      body: text
    })], { type: 'application/json' });
  },

  receiptCameraStream: null,
  receiptCameraPhoto: null,
  receiptCameraGeneration: 0,

  async startReceiptCamera(video) {
    this.resetReceiptCamera();
    const generation = this.receiptCameraGeneration;
    if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) {
      return 'Camera access requires HTTPS or localhost in a supported browser. You can still choose a photo.';
    }

    try {
      const stream = await navigator.mediaDevices.getUserMedia({
        audio: false, video: { facingMode: { ideal: 'environment' } }
      });
      if (generation !== this.receiptCameraGeneration || !video.isConnected) {
        stream.getTracks().forEach(track => track.stop());
        return null;
      }
      this.receiptCameraStream = stream;
      video.srcObject = stream;
      await video.play();
      return null;
    } catch (error) {
      if (generation !== this.receiptCameraGeneration) return null;
      this.resetReceiptCamera();
      if (error.name === 'NotAllowedError') return 'Camera access was denied. Allow it in your browser settings or choose a photo.';
      if (error.name === 'NotFoundError') return 'No camera was found. Choose an existing photo instead.';
      return 'The camera could not be opened. Check whether another app is using it, or choose a photo.';
    }
  },

  async takeReceiptPhoto(video, canvas) {
    if (!this.receiptCameraStream || !video.videoWidth || !video.videoHeight) return false;
    const generation = this.receiptCameraGeneration;
    const landscape = video.videoWidth >= video.videoHeight;
    const scale = Math.min(1, (landscape ? 1920 : 1080) / video.videoWidth,
      (landscape ? 1080 : 1920) / video.videoHeight);
    canvas.width = Math.max(1, Math.floor(video.videoWidth * scale));
    canvas.height = Math.max(1, Math.floor(video.videoHeight * scale));
    canvas.getContext('2d').drawImage(video, 0, 0, canvas.width, canvas.height);
    const photo = await new Promise(resolve => canvas.toBlob(resolve, 'image/jpeg', 0.92));
    if (!photo || generation !== this.receiptCameraGeneration) return false;
    this.resetReceiptCamera();
    video.srcObject = null;
    this.receiptCameraPhoto = photo;
    return true;
  },

  resetReceiptCamera() {
    this.receiptCameraGeneration++;
    this.receiptCameraStream?.getTracks().forEach(track => track.stop());
    this.receiptCameraStream = null;
    this.receiptCameraPhoto = null;
  },

  async uploadReceiptPhoto(url, fields) {
    if (!this.receiptCameraPhoto) throw new Error('No receipt photo is available.');
    return this.uploadBlob(url, fields, 'receipt.jpg', this.receiptCameraPhoto);
  },

  async uploadFile(url, fields, fileName, contentType, base64Bytes) {
    const bin = atob(base64Bytes);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) {
      bytes[i] = bin.charCodeAt(i);
    }
    const blob = new Blob([bytes], { type: contentType || 'application/octet-stream' });
    return this.uploadBlob(url, fields, fileName, blob);
  },

  async uploadBlob(url, fields, fileName, blob) {
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

window.lavariInterop.colorSchemeQuery.addEventListener('change', () => {
  if (window.lavariInterop.appearanceMode === 'system') {
    window.lavariInterop.setAppearance('system', window.lavariInterop.appearanceColor);
  }
});
