(() => {
  const origin = location.origin;
  if (window.top !== window || !/^https?:$/.test(location.protocol)) return;

  let site;
  let signedIn = false;
  let host;
  let status;
  let approveButton;
  let resendButton;
  let busy = false;
  let dismissed = false;
  let timer;

  const visible = (element) => Boolean(element && element.getClientRects().length && getComputedStyle(element).visibility !== 'hidden');
  const textOf = (element) => `${element.innerText || ''} ${element.getAttribute?.('aria-label') || ''}`.trim().toLowerCase();

  function pageState() {
    const bodyText = (document.body?.innerText || '').toLowerCase();
    const urlLooksSignedOut = /\/(login|log-in|signin|sign-in|auth|authenticate|session-expired)(\/|$)/i.test(location.pathname);
    const signOutControl = [...document.querySelectorAll('a,button,[role="button"]')]
      .some((el) => visible(el) && /\b(sign out|log out|logout)\b/.test(textOf(el)));
    const loginControl = [...document.querySelectorAll('button,input[type="submit"],[role="button"]')]
      .some((el) => visible(el) && /\b(sign in|log in|login|continue with email)\b/.test(textOf(el)));
    const loginLink = [...document.querySelectorAll('a')]
      .some((el) => visible(el) && /\b(sign in|log in|login)\b/.test(textOf(el)));
    const password = [...document.querySelectorAll('input[type="password"]')].find(visible);
    const emailInput = [...document.querySelectorAll('input:not([type="password"]),textarea')].find((el) => {
      if (!visible(el) || el.disabled || el.type === 'hidden') return false;
      const hint = `${el.type} ${el.name} ${el.id} ${el.autocomplete} ${el.placeholder}`.toLowerCase();
      return el.type === 'email' || /email|user(name)?|identifier/.test(hint);
    });
    const loginForm = Boolean(password && (password.closest('form') || loginControl || emailInput));
    const explicitSignedOut = /\b(session expired|you are signed out|you have been logged out|please sign in|sign in to continue)\b/.test(bodyText);

    // A visible login form is the strongest cross-site signal. Email-only login
    // steps, explicit signed-out messages, and /login routes cover common SPA/SSO flows.
    const isSignedOut = !signOutControl && (loginForm || explicitSignedOut || (urlLooksSignedOut && (loginControl || emailInput || bodyText.length < 5000)) || (loginLink && emailInput));
    return { isSignedOut, password, emailInput };
  }

  function removePrompt(resetDismissal = true) {
    host?.remove();
    host = status = approveButton = resendButton = undefined;
    if (resetDismissal) dismissed = false;
  }

  function showPrompt() {
    if (!site || dismissed || host || !pageState().isSignedOut) return;
    host = document.createElement('div');
    host.style.cssText = 'all:initial;position:fixed;top:16px;right:16px;z-index:2147483647;font:14px/1.4 system-ui,sans-serif;color:#172033;';
    const shadow = host.attachShadow({ mode: 'closed' });
    const card = document.createElement('section');
    card.style.cssText = 'box-sizing:border-box;width:min(340px,calc(100vw - 32px));padding:16px;border:1px solid #d7deea;border-radius:12px;background:#fff;box-shadow:0 8px 28px #10213b33;';
    const title = document.createElement('strong');
    title.textContent = 'Authenticate with Safe Passage?';
    title.style.cssText = 'font-size:15px;';
    const description = document.createElement('div');
    description.textContent = `Approve this ${site.name} sign-in on your trusted Safe Passage device.`;
    description.style.cssText = 'margin-top:5px;color:#526078;';
    status = document.createElement('div');
    status.setAttribute('role', 'status');
    status.style.cssText = 'margin-top:10px;color:#526078;';
    const actions = document.createElement('div');
    actions.style.cssText = 'display:flex;gap:8px;margin-top:12px;';
    approveButton = document.createElement('button');
    approveButton.textContent = 'Request approval';
    approveButton.style.cssText = 'border:0;border-radius:7px;padding:9px 12px;background:#3457d5;color:white;font-weight:600;cursor:pointer;';
    resendButton = document.createElement('button');
    resendButton.textContent = 'Resend request';
    resendButton.hidden = true;
    resendButton.style.cssText = 'border:1px solid #ccd4e2;border-radius:7px;padding:8px 10px;background:white;cursor:pointer;';
    const close = document.createElement('button');
    close.textContent = '×';
    close.setAttribute('aria-label', 'Dismiss Safe Passage prompt');
    close.style.cssText = 'position:absolute;right:10px;top:8px;border:0;background:transparent;color:#526078;font-size:20px;cursor:pointer;';
    card.style.position = 'relative';
    card.append(title, description, status, actions, close);
    actions.append(approveButton, resendButton);
    shadow.append(card);
    (document.body || document.documentElement).append(host);

    if (!signedIn) status.textContent = 'Sign in through the Safe Passage extension menu first.';
    close.addEventListener('click', () => { dismissed = true; removePrompt(false); });
    approveButton.addEventListener('click', requestApproval);
    resendButton.addEventListener('click', resendApproval);
  }

  async function requestApproval() {
    if (busy) return;
    busy = true;
    approveButton.disabled = true;
    approveButton.textContent = 'Waiting for approval…';
    status.textContent = 'Check Safe Passage on your trusted device.';
    try {
      const result = await sendMessage({ type: 'AUTHENTICATE_AND_GET_CREDENTIALS', origin });
      if (result.username || result.password) fillCredentials(result);
      status.textContent = result.username || result.password
        ? 'Approved. Saved credentials were filled in.'
        : 'Approved. Continue signing in to this site.';
      approveButton.textContent = 'Approved';
      resendButton.hidden = true;
    } catch (error) {
      status.textContent = error.message || 'Could not request approval.';
      approveButton.disabled = false;
      approveButton.textContent = 'Try again';
    } finally {
      busy = false;
    }
  }

  async function resendApproval() {
    resendButton.disabled = true;
    try {
      const result = await sendMessage({ type: 'RESEND_SITE_APPROVAL', origin });
      status.textContent = result.message || 'Approval request resent.';
    } catch (error) {
      status.textContent = error.message || 'Could not resend approval.';
    } finally {
      resendButton.disabled = false;
    }
  }

  function fillCredentials({ username, password }) {
    const state = pageState();
    const passwordInput = state.password;
    const usernameInput = state.emailInput || (passwordInput?.closest('form')
      ? [...passwordInput.closest('form').querySelectorAll('input[type="text"]')].find(visible)
      : null);
    if (usernameInput && username) setValue(usernameInput, username);
    if (passwordInput && password) setValue(passwordInput, password);
  }

  function setValue(input, value) {
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
    setter?.call(input, value);
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
  }

  function sendMessage(message) {
    return new Promise((resolve, reject) => chrome.runtime.sendMessage(message, (response) => {
      if (chrome.runtime.lastError) return reject(new Error('Could not reach Safe Passage extension. Refresh this page and try again.'));
      if (!response?.ok) return reject(new Error(response?.error || 'Safe Passage request failed.'));
      resolve(response);
    }));
  }

  function refreshPrompt() {
    clearTimeout(timer);
    timer = setTimeout(() => {
      if (!pageState().isSignedOut) {
        removePrompt();
        return;
      }
      showPrompt();
    }, 300);
  }

  function checkSite() {
    chrome.runtime.sendMessage({ type: 'CHECK_ORIGIN', origin }, (response) => {
      if (chrome.runtime.lastError || !response?.matched) {
        site = null;
        removePrompt();
        return;
      }
      site = response.site;
      signedIn = Boolean(response.signedIn);
      refreshPrompt();
    });
  }

  checkSite();
  new MutationObserver(refreshPrompt).observe(document.documentElement, {
    childList: true, subtree: true, attributes: true,
    attributeFilter: ['type', 'style', 'hidden', 'aria-label'],
  });

  chrome.runtime.onMessage.addListener((message) => {
    if (message.type === 'APPROVAL_PENDING') {
      if (status) status.textContent = 'Approval request sent. Open Safe Passage on your trusted device.';
      if (resendButton) resendButton.hidden = false;
    }
    if (message.type === 'SITE_ACCESS_RETRY' || message.type === 'START_SITE_GATE') checkSite();
    if (message.type === 'AUTOFILL_NOW') fillCredentials(message);
  });
})();
