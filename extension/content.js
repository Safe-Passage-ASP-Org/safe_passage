(() => {
  if (window.top !== window || !/^https?:$/.test(location.protocol)) return;

  const origin = location.origin;
  let site = null;
  let signedIn = false;
  let promptRoot = null;
  let statusElement = null;
  let busy = false;
  let dismissed = false;
  let debounceId;
  let resendButton = null;

  function loginFormPresent() {
    return [...document.querySelectorAll('input[type="password"]')].some((input) => {
      if (!input.isConnected || input.disabled || input.getAttribute('aria-hidden') === 'true') return false;
      const form = input.closest('form');
      return !form || getComputedStyle(input).display !== 'none';
    });
  }

  function removePrompt() {
    promptRoot?.remove();
    promptRoot = null;
    statusElement = null;
    dismissed = false;
  }

  function setMessage(message) {
    if (statusElement) statusElement.textContent = message;
  }

  function showPrompt() {
    if (!site || !loginFormPresent() || dismissed || promptRoot) return;

    promptRoot = document.createElement('div');
    promptRoot.id = 'safe-passage-login-prompt';
    promptRoot.style.cssText = 'all:initial;position:fixed;top:16px;right:16px;z-index:2147483647;font:14px/1.4 system-ui,sans-serif;color:#172033;';
    const shadow = promptRoot.attachShadow({ mode: 'closed' });
    const card = document.createElement('div');
    card.style.cssText = 'box-sizing:border-box;width:320px;padding:16px;border:1px solid #d7deea;border-radius:12px;background:#fff;box-shadow:0 8px 28px #10213b33;';
    card.innerHTML = `<div style="display:flex;align-items:flex-start;gap:10px"><div style="flex:1"><strong style="font-size:15px">Authenticate with Safe Passage?</strong><div style="margin-top:4px;color:#526078">Approve this ${escapeHtml(site.name)} login on your trusted Safe Passage device.</div></div><button data-sp-close aria-label="Dismiss" style="border:0;background:transparent;font-size:20px;cursor:pointer;color:#526078">×</button></div><div data-sp-status style="margin-top:10px;color:#526078"></div><div style="display:flex;gap:8px;margin-top:12px"><button data-sp-approve style="border:0;border-radius:7px;padding:9px 12px;background:#3457d5;color:white;font-weight:600;cursor:pointer">Request approval</button><button data-sp-resend hidden style="border:1px solid #ccd4e2;border-radius:7px;padding:8px 10px;background:white;cursor:pointer">Resend request</button></div>`;
    shadow.append(card);
    resendButton = card.querySelector('[data-sp-resend]');
    statusElement = card.querySelector('[data-sp-status]');
    if (!signedIn) setMessage('Sign in from the Safe Passage extension menu to request approval.');
    (document.body || document.documentElement).append(promptRoot);

    card.querySelector('[data-sp-close]').addEventListener('click', () => {
      dismissed = true;
      promptRoot?.remove();
      promptRoot = null;
    });
    card.querySelector('[data-sp-approve]').addEventListener('click', async () => {
      if (busy) return;
      busy = true;
      const button = card.querySelector('[data-sp-approve]');
      button.disabled = true;
      button.textContent = 'Waiting for approval…';
      setMessage('Check Safe Passage on your trusted device.');
      try {
        const result = await sendMessage({ type: 'AUTHENTICATE_AND_GET_CREDENTIALS', origin });
        const credentials = result || {};
        if (credentials.username || credentials.password) fillLogin(credentials);
        setMessage(credentials.username || credentials.password
          ? 'Approved. Your saved credentials were filled in.'
          : 'Approved. Continue signing in to this site.');
        button.textContent = 'Approved';
        card.querySelector('[data-sp-resend]').hidden = true;
      } catch (error) {
        setMessage(error.message || 'Could not request approval.');
        button.disabled = false;
        button.textContent = 'Try again';
      } finally {
        busy = false;
      }
    });
    card.querySelector('[data-sp-resend]').addEventListener('click', async (event) => {
      const button = event.currentTarget;
      button.disabled = true;
      try {
        const result = await sendMessage({ type: 'RESEND_SITE_APPROVAL', origin });
        setMessage(result.message || 'Approval request resent.');
      } catch (error) {
        setMessage(error.message || 'Could not resend the request.');
      } finally {
        button.disabled = false;
      }
    });
  }

  function fillLogin({ username, password }) {
    const passwordInput = [...document.querySelectorAll('input[type="password"]')].find((input) => input.isConnected);
    if (passwordInput && password) setInputValue(passwordInput, password);
    if (username) {
      const candidates = [...document.querySelectorAll('input')].filter((input) => {
        if (!input.isConnected || input.type === 'password' || input.type === 'hidden' || input.disabled) return false;
        const hint = `${input.type} ${input.name} ${input.id} ${input.autocomplete} ${input.placeholder}`.toLowerCase();
        return /email|user|login|identifier/.test(hint) || input.type === 'email';
      });
      const usernameInput = candidates.find((input) => !passwordInput || input.form === passwordInput.form) || candidates[0];
      if (usernameInput) setInputValue(usernameInput, username);
    }
  }

  function setInputValue(input, value) {
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
    setter?.call(input, value);
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
  }

  function escapeHtml(value) {
    return String(value).replace(/[&<>"']/g, (char) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]);
  }

  function sendMessage(message) {
    return new Promise((resolve, reject) => {
      chrome.runtime.sendMessage(message, (response) => {
        if (chrome.runtime.lastError) return reject(new Error('Could not reach Safe Passage extension. Refresh this page and try again.'));
        if (!response?.ok) return reject(new Error(response?.error || 'Safe Passage request failed.'));
        resolve(response);
      });
    });
  }

  function syncPrompt() {
    clearTimeout(debounceId);
    debounceId = setTimeout(() => {
      if (!loginFormPresent()) {
        removePrompt();
        return;
      }
      showPrompt();
    }, 250);
  }

  function checkOrigin() {
    chrome.runtime.sendMessage({ type: 'CHECK_ORIGIN', origin }, (response) => {
      if (chrome.runtime.lastError || !response?.matched) {
        site = null;
        removePrompt();
        return;
      }
      site = response.site;
      signedIn = Boolean(response.signedIn);
      syncPrompt();
    });
  }

  checkOrigin();
  new MutationObserver(syncPrompt).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['type', 'style', 'hidden'] });

  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (message.type === 'APPROVAL_PENDING' && promptRoot) {
      setMessage('Approval request sent. Open Safe Passage on your trusted device.');
      if (resendButton) resendButton.hidden = false;
    }
    if (message.type === 'SITE_ACCESS_RETRY' || message.type === 'START_SITE_GATE') checkOrigin();
    if (message.type === 'AUTOFILL_NOW') {
      fillLogin(message);
      sendResponse({ ok: true });
    }
  });
})();
