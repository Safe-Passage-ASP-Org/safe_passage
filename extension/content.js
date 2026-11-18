(function () {
  const GATE_ID = 'safepassage-extension-gate';
  let currentOrigin = window.location.origin;
  let currentSite = null;
  let signedIn = false;
  let flowRunning = false;
  let approvalPending = false;
  let gateRoot = null;
  let statusElement = null;
  let primaryButton = null;
  let resendButton = null;

  function findLoginInputs() {
    const passwordInput = document.querySelector('input[type="password"]:not([disabled])');
    if (!passwordInput) return null;

    const form = passwordInput.closest('form') || document;
    const userInput = form.querySelector(
      'input[autocomplete="username"], input[type="email"], input[name*="user" i], input[name*="email" i], input[type="text"]',
    );
    return { userInput, passwordInput };
  }

  function setInputValue(element, value) {
    if (!element || !value) return;
    const descriptor = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value');
    if (descriptor?.set) descriptor.set.call(element, value);
    else element.value = value;
    element.dispatchEvent(new Event('input', { bubbles: true }));
    element.dispatchEvent(new Event('change', { bubbles: true }));
  }

  function showGate(siteName) {
    let host = document.getElementById(GATE_ID);
    if (!host) {
      host = document.createElement('div');
      host.id = GATE_ID;
      gateRoot = host.attachShadow({ mode: 'closed' });
      gateRoot.innerHTML = `
        <style>
          :host { all: initial; }
          .backdrop { position: fixed; inset: 0; z-index: 2147483647; display: grid; place-items: center; padding: 24px; background: rgba(3, 7, 18, .88); color: #e5e7eb; font: 14px/1.5 Inter, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; }
          .card { width: min(100%, 390px); box-sizing: border-box; padding: 26px; border: 1px solid #334155; border-radius: 14px; background: #0f172a; box-shadow: 0 24px 80px rgba(0,0,0,.55); }
          .brand { margin: 0 0 8px; color: #38bdf8; font-size: 11px; font-weight: 800; letter-spacing: .12em; text-transform: uppercase; }
          h1 { margin: 0 0 10px; color: #f8fafc; font-size: 21px; }
          p { margin: 0 0 16px; color: #cbd5e1; }
          .status { min-height: 22px; color: #7dd3fc; }
          .status.error { color: #fca5a5; }
          button { width: 100%; margin-top: 10px; border: 1px solid #0284c7; border-radius: 8px; padding: 11px 14px; background: #0369a1; color: white; font: inherit; font-weight: 700; cursor: pointer; }
          button.secondary { border-color: #475569; background: #1e293b; }
          button:disabled { opacity: .55; cursor: wait; }
          .hint { margin: 14px 0 0; color: #94a3b8; font-size: 12px; }
        </style>
        <div class="backdrop" role="dialog" aria-modal="true" aria-labelledby="sp-gate-title">
          <section class="card">
            <p class="brand">Safe Passage</p>
            <h1 id="sp-gate-title">Approve access to this app</h1>
            <p id="sp-gate-description"></p>
            <p class="status" role="status" aria-live="polite"></p>
            <button id="sp-gate-primary" type="button">Try again</button>
            <button id="sp-gate-resend" class="secondary" type="button" hidden>Resend approval request</button>
            <p class="hint">Approve the request in the Safe Passage mobile app. This page stays locked until approval.</p>
          </section>
        </div>`;
      document.documentElement.appendChild(host);
      statusElement = gateRoot.querySelector('.status');
      primaryButton = gateRoot.getElementById('sp-gate-primary');
      resendButton = gateRoot.getElementById('sp-gate-resend');
      primaryButton.addEventListener('click', startApprovalFlow);
      resendButton.addEventListener('click', resendApproval);
    }

    gateRoot.getElementById('sp-gate-description').textContent =
      signedIn
        ? `Safe Passage is checking your access to ${siteName}. Approve the request on your trusted device.`
        : 'Sign in to Safe Passage from the extension menu, then return here to request approval.';
    setStatus(signedIn ? 'Preparing approval request…' : 'Extension sign-in required.');
    primaryButton.textContent = signedIn ? 'Request approval again' : 'Retry after signing in';
    resendButton.hidden = true;
    return host;
  }

  function setStatus(message, isError = false) {
    if (!statusElement) return;
    statusElement.textContent = message;
    statusElement.classList.toggle('error', isError);
  }

  function unlockPage(credentials) {
    if (credentials) {
      const inputs = findLoginInputs();
      if (inputs) {
        setInputValue(inputs.userInput, credentials.username);
        setInputValue(inputs.passwordInput, credentials.password);
      }
    }
    document.getElementById(GATE_ID)?.remove();
    gateRoot = null;
    statusElement = null;
    primaryButton = null;
    resendButton = null;
    approvalPending = false;
  }

  function startApprovalFlow() {
    if (flowRunning) return;
    if (!signedIn) {
      setStatus('Open the Safe Passage extension menu and sign in first.', true);
      return;
    }

    flowRunning = true;
    approvalPending = false;
    primaryButton.disabled = true;
    resendButton.hidden = true;
    setStatus('Sending an approval request to your trusted device…');

    chrome.runtime.sendMessage(
      { type: 'AUTHENTICATE_AND_GET_CREDENTIALS', origin: currentOrigin },
      (response) => {
        flowRunning = false;
        if (chrome.runtime.lastError) {
          primaryButton.disabled = false;
          setStatus(chrome.runtime.lastError.message, true);
          return;
        }
        if (!response?.ok) {
          primaryButton.disabled = false;
          approvalPending = false;
          resendButton.hidden = true;
          if (response?.error?.toLowerCase().includes('session expired')) {
            signedIn = false;
            primaryButton.textContent = 'Retry after signing in';
          }
          setStatus(response?.error || 'Approval failed. Try again.', true);
          return;
        }
        unlockPage(response);
      },
    );
  }

  function resendApproval() {
    if (!approvalPending) return;
    resendButton.disabled = true;
    setStatus('Resending the approval request…');
    chrome.runtime.sendMessage({ type: 'RESEND_SITE_APPROVAL', origin: currentOrigin }, (response) => {
      resendButton.disabled = false;
      if (chrome.runtime.lastError || !response?.ok) {
        setStatus(response?.error || chrome.runtime.lastError?.message || 'Could not resend the request.', true);
        return;
      }
      setStatus(response.message || 'Approval request resent.');
    });
  }

  function initializeGate() {
    chrome.runtime.sendMessage({ type: 'CHECK_ORIGIN', origin: currentOrigin }, (response) => {
      if (chrome.runtime.lastError || !response?.ok || !response.matched) return;
      currentSite = response.site;
      signedIn = response.signedIn;
      showGate(currentSite.name);
      if (signedIn) startApprovalFlow();
    });
  }

  chrome.runtime.onMessage.addListener((message) => {
    if (message.type === 'APPROVAL_PENDING') {
      approvalPending = true;
      if (primaryButton) primaryButton.disabled = true;
      if (resendButton) resendButton.hidden = false;
      setStatus('Waiting for approval in Safe Passage Mobile…');
    }

    if (message.type === 'SITE_ACCESS_RETRY') {
      if (!currentSite) return;
      signedIn = message.signedIn !== false;
      showGate(currentSite.name);
      startApprovalFlow();
    }

    if (message.type === 'START_SITE_GATE') {
      signedIn = message.signedIn !== false;
      currentSite = { ...(currentSite || {}), name: message.siteName || window.location.hostname };
      showGate(currentSite.name);
      startApprovalFlow();
    }
  });

  initializeGate();
})();
