(() => {
  const origin = location.origin;
  if (!/^https?:$/.test(location.protocol)) return;

  let site;
  let signedIn = false;
  let host;
  let status;
  let approveButton;
  let resendButton;
  let busy = false;
  let dismissed = false;
  let mobileApproved = false;
  let loadingTotp = false;
  let loadingCredentials = false;
  let lastTotpAttempt = 0;
  let lastCredentialAttempt = 0;
  let timer;

  const visible = (element) => Boolean(element && element.getClientRects().length && getComputedStyle(element).visibility !== 'hidden');
  const textOf = (element) => `${element.innerText || ''} ${element.value || ''} ${element.getAttribute?.('aria-label') || ''}`.trim().toLowerCase();

  function pageState() {
    const bodyText = (document.body?.innerText || '').toLowerCase();
    const urlLooksSignedOut = /\/(login|log-in|signin|sign-in|auth|authenticate|session-expired)(\/|$)/i.test(location.pathname);
    const signOutControl = [...document.querySelectorAll('a,button,[role="button"]')]
      .some((el) => visible(el) && /\b(sign out|log out|logout)\b/.test(textOf(el)));
    const loginControl = [...document.querySelectorAll('button,input[type="submit"],input[type="button"],[role="button"]')]
      .some((el) => visible(el) && /\b(sign in|log in|login|continue with email)\b/.test(textOf(el)));
    const loginLink = [...document.querySelectorAll('a')]
      .some((el) => visible(el) && /\b(sign in|log in|login)\b/.test(textOf(el)));
    const password = [...document.querySelectorAll('input[type="password"]')].find(visible);
    const totpInput = [...document.querySelectorAll('input:not([type="password"]),textarea')].find((el) => {
      if (!visible(el) || el.disabled || el.type === 'hidden') return false;
      const hint = `${el.name} ${el.id} ${el.autocomplete} ${el.placeholder} ${el.getAttribute('aria-label')}`.toLowerCase();
      return el.autocomplete === 'one-time-code' || /one.?time|2fa|mfa|auth(?:entication)?[-_ ]?code|verification[-_ ]?code|otp/.test(hint);
    });
    const emailInput = [...document.querySelectorAll('input:not([type="password"]),textarea')].find((el) => {
      if (!visible(el) || el.disabled || el.type === 'hidden') return false;
      const hint = `${el.type} ${el.name} ${el.id} ${el.autocomplete} ${el.placeholder}`.toLowerCase();
      return el.type === 'email' || /email|user(name)?|identifier/.test(hint);
    });
    const loginForm = Boolean(password && (password.closest('form') || loginControl || emailInput));
    const emailLoginForm = Boolean(emailInput && loginControl && emailInput.closest('form'));
    const explicitSignedOut = /\b(session expired|you are signed out|you have been logged out|please sign in|sign in to continue)\b/.test(bodyText);

    // A visible login form is the strongest cross-site signal. Email-only login
    // steps, explicit signed-out messages, and /login routes cover common SPA/SSO flows.
    const isSignedOut = !signOutControl && (loginForm || emailLoginForm || explicitSignedOut || (urlLooksSignedOut && (loginControl || emailInput)) || (loginLink && emailInput));
    return { isSignedOut, password, emailInput, totpInput };
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
    description.textContent = site.lockedByPlan
      ? `${site.name} is over the Free plan's saved-website limit. Upgrade to Pro or remove another saved website to use it.`
      : site.isVerified === false
      ? `${site.name} is waiting for Safe Passage verification. Credentials and 2FA are disabled until an administrator approves this site.`
      : mobileApproved
        ? `Safe Passage approval is active for this ${site.name} sign-in.`
        : `Approve this ${site.name} sign-in on your trusted Safe Passage device.`;
    description.style.cssText = 'margin-top:5px;color:#526078;';
    status = document.createElement('div');
    status.setAttribute('role', 'status');
    status.style.cssText = 'margin-top:10px;color:#526078;';
    const actions = document.createElement('div');
    actions.style.cssText = 'display:flex;gap:8px;margin-top:12px;';
    approveButton = document.createElement('button');
    approveButton.textContent = 'Request approval';
    if (mobileApproved) {
      approveButton.disabled = true;
      approveButton.textContent = 'Approved';
      status.textContent = 'Mobile approval is active for this sign-in.';
    }
    if (site.lockedByPlan) {
      approveButton.disabled = true;
      approveButton.textContent = 'Pro required';
      status.textContent = 'This site is saved, but its credentials are locked by the current plan.';
    } else if (site.isVerified === false) {
      approveButton.disabled = true;
      approveButton.textContent = 'Verification pending';
    }
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
      mobileApproved = true;
      if (result.username || result.password) fillCredentials(result);
      status.textContent = result.requiresProForTotp
        ? 'Username and password were filled. Authenticator codes require an active Pro subscription.'
        : result.username || result.password
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

  function fillCredentials({ username, password, totpCode }) {
    const state = pageState();
    const passwordInput = state.password;
    const usernameInput = state.emailInput || (passwordInput?.closest('form')
      ? [...passwordInput.closest('form').querySelectorAll('input[type="text"]')].find(visible)
      : null);
    if (usernameInput && username && !usernameInput.value) setValue(usernameInput, username);
    if (passwordInput && password && !passwordInput.value) setValue(passwordInput, password);
    if (totpCode) fillTotpCode(totpCode);
  }

  function fillTotpCode(code) {
    const input = pageState().totpInput;
    if (input && code && !input.value) setValue(input, code);
  }

  async function fillApprovedCredentialsIfAvailable() {
    if (!mobileApproved || loadingCredentials || Date.now() - lastCredentialAttempt < 10000) return;
    const state = pageState();
    const passwordInput = state.password;
    const usernameInput = state.emailInput || (passwordInput?.closest('form')
      ? [...passwordInput.closest('form').querySelectorAll('input[type="text"]')].find(visible)
      : null);
    if ((!passwordInput || passwordInput.value) && (!usernameInput || usernameInput.value)) return;
    loadingCredentials = true;
    lastCredentialAttempt = Date.now();
    try {
      const result = await sendMessage({ type: 'GET_APPROVED_CREDENTIALS', origin });
      fillCredentials(result);
    } catch { /* Never fill unless an approved Safe Passage session is available. */ }
    finally { loadingCredentials = false; }
  }

  async function fillTotpIfAvailable() {
    if (!mobileApproved || loadingTotp || Date.now() - lastTotpAttempt < 15000) return;
    const input = pageState().totpInput;
    if (!input || input.value) return;
    loadingTotp = true;
    lastTotpAttempt = Date.now();
    try {
      const result = await sendMessage({ type: 'GET_CURRENT_TOTP', origin });
      if (result.totpCode) fillTotpCode(result.totpCode);
    } catch { /* The visible site prompt reports approval and vault errors. */ }
    finally { loadingTotp = false; }
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
    timer = setTimeout(async () => {
      if (!pageState().isSignedOut) {
        removePrompt();
        await fillApprovedCredentialsIfAvailable();
        await fillTotpIfAvailable();
        return;
      }
      showPrompt();
      await fillApprovedCredentialsIfAvailable();
      await fillTotpIfAvailable();
    }, 300);
  }

  function checkSite() {
    chrome.runtime.sendMessage({ type: 'CHECK_ORIGIN', origin }, (response) => {
      if (chrome.runtime.lastError || !response?.matched) {
        site = null;
        mobileApproved = false;
        removePrompt();
        return;
      }
      site = response.site;
      mobileApproved = response.approved === true;
      signedIn = Boolean(response.signedIn);
      refreshPrompt();
    });
  }

  function onSpaNavigation() {
    refreshPrompt();
    if (!site) checkSite();
  }

  checkSite();
  new MutationObserver(refreshPrompt).observe(document.documentElement, {
    childList: true, subtree: true, attributes: true, characterData: true,
    attributeFilter: ['type', 'style', 'hidden', 'aria-label'],
  });
  window.addEventListener('popstate', onSpaNavigation);
  window.addEventListener('hashchange', onSpaNavigation);
  for (const method of ['pushState', 'replaceState']) {
    const original = history[method].bind(history);
    history[method] = (...args) => {
      const result = original(...args);
      onSpaNavigation();
      return result;
    };
  }

  chrome.runtime.onMessage.addListener((message) => {
    if (message.type === 'APPROVAL_PENDING') {
      if (status) status.textContent = 'Approval request sent. Open Safe Passage on your trusted device.';
      if (resendButton) resendButton.hidden = false;
    }
    if (message.type === 'SITE_ACCESS_RETRY' || message.type === 'START_SITE_GATE') checkSite();
    if (message.type === 'AUTOFILL_NOW') fillCredentials(message);
  });
})();
