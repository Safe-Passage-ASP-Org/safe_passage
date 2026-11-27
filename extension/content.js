(() => {
  const origin = location.origin;
  if (!/^https?:$/.test(location.protocol) || window.top !== window) return;

  let site;
  let signedIn = false;
  let mobileApproved = false;
  let gateHost;
  let gateStatus;
  let gateButton;
  let manualCode;
  let manualCodeButton;
  let busy = false;
  let loadingCredentials = false;
  let loadingTotp = false;
  let lastCredentialAttempt = 0;
  let lastTotpAttempt = 0;
  let initialAuthUrl = '';
  let refreshTimer;
  const inertNodes = new Map();

  const visible = (element) => Boolean(element && element.getClientRects().length && getComputedStyle(element).visibility !== 'hidden');
  const textOf = (element) => `${element.innerText || ''} ${element.value || ''} ${element.getAttribute?.('aria-label') || ''}`.trim().toLowerCase();

  function pageState() {
    const bodyText = (document.body?.innerText || '').toLowerCase();
    const urlLooksSignedOut = /\/(login|log-in|signin|sign-in|auth|authenticate|session-expired)(\/|$)/i.test(location.pathname);
    const signOutControl = [...document.querySelectorAll('a,button,[role="button"]')]
      .some((el) => visible(el) && /\b(sign out|log out|logout)\b/.test(textOf(el)));
    const loginControl = [...document.querySelectorAll('button,input[type="submit"],input[type="button"],[role="button"]')]
      .some((el) => visible(el) && /\b(sign in|log in|login|continue with email|verify|submit)\b/.test(textOf(el)));
    const loginLink = [...document.querySelectorAll('a')]
      .some((el) => visible(el) && /\b(sign in|log in|login)\b/.test(textOf(el)));
    const password = [...document.querySelectorAll('input[type="password"]')].find(visible);
    const authInput = [...document.querySelectorAll('input:not([type="password"]),textarea')].find((el) => {
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
    const mfaForm = Boolean(authInput && (loginControl || authInput.closest('form')));
    const explicitSignedOut = /\b(session expired|you are signed out|you have been logged out|please sign in|sign in to continue)\b/.test(bodyText);
    const isSignedOut = !signOutControl && (loginForm || emailLoginForm || mfaForm || explicitSignedOut || (urlLooksSignedOut && (loginControl || emailInput)) || (loginLink && emailInput));
    const signedInEvidence = signOutControl || (!isSignedOut && initialAuthUrl && location.href !== initialAuthUrl && /\b(welcome|dashboard|my account|profile|signed in)\b/.test(bodyText));
    return { isSignedOut, signedInEvidence, password, emailInput, authInput, loginControl };
  }

  function sendMessage(message) {
    return new Promise((resolve, reject) => chrome.runtime.sendMessage(message, (response) => {
      if (chrome.runtime.lastError) return reject(new Error('Could not reach Safe Passage. Refresh this page and try again.'));
      if (!response?.ok) return reject(new Error(response?.error || 'Safe Passage request failed.'));
      resolve(response);
    }));
  }

  function reportStatus(status) {
    sendMessage({ type: 'SITE_GATE_STATUS', origin, status, siteName: site?.name }).catch(() => {});
    if (gateStatus) gateStatus.textContent = status;
  }

  function setPageBlocked(blocked) {
    if (!document.body) return;
    for (const child of [...document.body.children]) {
      if (child === gateHost) continue;
      if (blocked) {
        if (!inertNodes.has(child)) {
          inertNodes.set(child, { inert: child.inert, hadMarker: child.hasAttribute('data-safe-passage-inert') });
          child.inert = true;
          child.setAttribute('data-safe-passage-inert', '');
        }
      } else if (inertNodes.has(child)) {
        const previous = inertNodes.get(child);
        inertNodes.delete(child);
        child.inert = previous.inert;
        if (!previous.hadMarker) child.removeAttribute('data-safe-passage-inert');
      }
    }
  }

  function removeGate() {
    gateHost?.remove();
    gateHost = gateStatus = gateButton = undefined;
    setPageBlocked(false);
  }

  function ensureGate() {
    if (!site || gateHost) return;
    gateHost = document.createElement('div');
    gateHost.id = 'safe-passage-site-gate';
    gateHost.style.cssText = 'all:initial;position:fixed;inset:0;z-index:2147483647;display:grid;place-items:center;padding:20px;box-sizing:border-box;background:rgba(9,14,24,.82);backdrop-filter:blur(5px);font:15px/1.5 system-ui,sans-serif;color:#172033;';
    const shadow = gateHost.attachShadow({ mode: 'closed' });
    const card = document.createElement('section');
    card.style.cssText = 'box-sizing:border-box;width:min(440px,100%);padding:25px;border:1px solid #d7deea;border-radius:16px;background:#fff;box-shadow:0 20px 70px #0008;text-align:center;';
    const brand = document.createElement('div');
    brand.textContent = 'SAFE PASSAGE';
    brand.style.cssText = 'font-size:11px;font-weight:800;letter-spacing:.12em;color:#3457d5;';
    const title = document.createElement('h1');
    title.textContent = 'Sign-in is locked';
    title.style.cssText = 'margin:9px 0 4px;font-size:23px;line-height:1.2;color:#172033;';
    const detail = document.createElement('p');
    detail.textContent = site.lockedByPlan
      ? `${site.name} exceeds the Free plan's saved-website limit. Upgrade to Pro or remove another saved website to continue.`
      : site.isVerified === false
      ? `${site.name} is awaiting Safe Passage verification. Credentials and 2FA cannot be used yet.`
      : `Complete Safe Passage approval and sign in to ${site.name} to unlock this page.`;
    detail.style.cssText = 'margin:8px 0;color:#526078;';
    gateStatus = document.createElement('p');
    gateStatus.setAttribute('role', 'status');
    gateStatus.style.cssText = 'min-height:24px;margin:15px 0;color:#344054;font-size:13px;';
    gateButton = document.createElement('button');
    gateButton.type = 'button';
    gateButton.style.cssText = 'width:100%;border:0;border-radius:8px;padding:12px;background:#3457d5;color:#fff;font-size:14px;font-weight:700;cursor:pointer;';
    gateButton.textContent = site.lockedByPlan ? 'Pro plan required' : site.isVerified === false ? 'Waiting for site verification' : (signedIn ? 'Request mobile approval' : 'Sign in to Safe Passage first');
    gateButton.disabled = site.lockedByPlan === true || site.isVerified === false || !signedIn;
    manualCode = document.createElement('input');
    manualCode.type = 'text';
    manualCode.inputMode = 'numeric';
    manualCode.autocomplete = 'one-time-code';
    manualCode.placeholder = 'Enter the site verification code';
    manualCode.style.cssText = 'display:none;box-sizing:border-box;width:100%;margin-top:9px;padding:11px;border:1px solid #ccd4e2;border-radius:8px;font:14px system-ui,sans-serif;';
    manualCodeButton = document.createElement('button');
    manualCodeButton.type = 'button';
    manualCodeButton.textContent = 'Submit verification code';
    manualCodeButton.style.cssText = 'display:none;width:100%;margin-top:7px;border:1px solid #ccd4e2;border-radius:8px;padding:10px;background:#fff;color:#172033;font-size:13px;font-weight:700;cursor:pointer;';
    const hint = document.createElement('p');
    hint.textContent = 'The Safe Passage side panel stays available while you switch tabs. This page unlocks only after successful sign-in is detected.';
    hint.style.cssText = 'margin:13px 0 0;color:#667085;font-size:12px;';
    card.append(brand, title, detail, gateStatus, gateButton, manualCode, manualCodeButton, hint);
    shadow.append(card);
    document.body.append(gateHost);
    gateButton.addEventListener('click', requestApproval);
    manualCodeButton.addEventListener('click', () => {
      const input = pageState().authInput;
      if (!input || !manualCode.value.trim()) {
        reportStatus('Enter the verification code requested by the site.');
        return;
      }
      setValue(input, manualCode.value.trim());
      manualCode.value = '';
      submitCurrentAuthStep();
      reportStatus('Submitting the site verification code…');
    });
    setPageBlocked(true);
  }

  function setValue(input, value) {
    if (!input || !value) return;
    const prototype = input instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    Object.getOwnPropertyDescriptor(prototype, 'value')?.set?.call(input, value);
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
  }

  function getUsernameInput(state) {
    return state.emailInput || (state.password?.closest('form')
      ? [...state.password.closest('form').querySelectorAll('input[type="text"]')].find(visible)
      : null);
  }

  function fillCredentials(credentials) {
    const state = pageState();
    const usernameInput = getUsernameInput(state);
    if (usernameInput && credentials.username) setValue(usernameInput, credentials.username);
    if (state.password && credentials.password) setValue(state.password, credentials.password);
    if (state.authInput && credentials.totpCode) setValue(state.authInput, credentials.totpCode);
  }

  function submitCurrentAuthStep() {
    const state = pageState();
    if (state.password && !state.password.value) return false;
    if (state.emailInput && !state.emailInput.value) return false;
    if (state.authInput && !state.authInput.value) return false;
    const field = state.password || state.authInput || state.emailInput;
    const form = field?.closest('form');
    if (form?.requestSubmit) {
      form.requestSubmit();
      return true;
    }
    const button = [...document.querySelectorAll('button,input[type="submit"],[role="button"]')]
      .find((element) => visible(element) && !element.disabled && /\b(sign in|log in|login|continue|verify|submit|next)\b/.test(textOf(element)));
    if (button) { button.click(); return true; }
    return false;
  }

  async function requestApproval() {
    if (busy || !site) return;
    if (site.lockedByPlan) {
      reportStatus('This saved website exceeds the Free plan limit. Upgrade to Pro or remove another saved website.');
      return;
    }
    if (!signedIn) {
      reportStatus('Sign in to Safe Passage in the side panel first.');
      return;
    }
    busy = true;
    gateButton.disabled = true;
    gateButton.textContent = 'Waiting for mobile approval…';
    reportStatus('Approve this sign-in on your trusted Safe Passage device.');
    try {
      const result = await sendMessage({ type: 'AUTHENTICATE_AND_GET_CREDENTIALS', origin, pageUrl: location.href });
      mobileApproved = true;
      fillCredentials(result);
      reportStatus(result.requiresProForTotp
        ? 'Password filled. Authenticator code generation requires Pro.'
        : 'Approved. Submitting the saved sign-in…');
      if (!result.username && !result.password && !pageState().authInput) {
        reportStatus('Approval succeeded, but no saved credentials are available. Add credentials in the Safe Passage side panel.');
        gateButton.textContent = 'Retry after saving credentials';
        gateButton.disabled = false;
        return;
      }
      submitCurrentAuthStep();
      gateButton.textContent = 'Checking site sign-in…';
    } catch (error) {
      reportStatus(error.message || 'Could not request approval.');
      gateButton.textContent = 'Try approval again';
      gateButton.disabled = false;
    } finally {
      busy = false;
    }
  }

  async function fillApprovedCredentialsIfAvailable() {
    if (!mobileApproved || loadingCredentials || Date.now() - lastCredentialAttempt < 4000) return;
    const state = pageState();
    const userInput = getUsernameInput(state);
    if ((!state.password || state.password.value) && (!userInput || userInput.value)) return;
    loadingCredentials = true;
    lastCredentialAttempt = Date.now();
    try {
      const result = await sendMessage({ type: 'GET_APPROVED_CREDENTIALS', origin, pageUrl: location.href });
      fillCredentials(result);
      if (submitCurrentAuthStep()) reportStatus('Submitting saved credentials…');
    } catch (error) { reportStatus(error.message); }
    finally { loadingCredentials = false; }
  }

  async function fillTotpIfAvailable() {
    if (!mobileApproved || loadingTotp || Date.now() - lastTotpAttempt < 4000) return;
    const state = pageState();
    if (!state.authInput || state.authInput.value) return;
    loadingTotp = true;
    lastTotpAttempt = Date.now();
    try {
      const result = await sendMessage({ type: 'GET_CURRENT_TOTP', origin, pageUrl: location.href });
      if (result.totpCode) {
        fillCredentials({ totpCode: result.totpCode });
        submitCurrentAuthStep();
        reportStatus('Submitting the approved verification code…');
      }
    } catch (error) { reportStatus(error.message); }
    finally { loadingTotp = false; }
  }

  async function refreshGate() {
    const state = pageState();
    if (!site) return;
    if (!initialAuthUrl && state.isSignedOut) initialAuthUrl = location.href;
    if (state.signedInEvidence) {
      reportStatus('Site sign-in detected. Unlocking this page.');
      removeGate();
      return;
    }
    if (state.isSignedOut || gateHost) {
      ensureGate();
      setPageBlocked(true);
      if (!signedIn) reportStatus('Sign in to Safe Passage in the side panel to begin.');
      else if (site.lockedByPlan) reportStatus('This saved website exceeds the Free plan limit. Upgrade to Pro or remove another saved website.');
      else if (site.isVerified === false) reportStatus('This site has not been verified by an administrator.');
      else if (!mobileApproved) reportStatus('Request approval and approve this sign-in on your trusted device.');
      else {
        reportStatus('Complete the site sign-in. The page stays locked until success is detected.');
        const hasMfaChallenge = Boolean(state.authInput);
        manualCode.style.display = hasMfaChallenge ? 'block' : 'none';
        manualCodeButton.style.display = hasMfaChallenge ? 'block' : 'none';
        await fillApprovedCredentialsIfAvailable();
        await fillTotpIfAvailable();
      }
    }
  }

  function scheduleRefresh() {
    clearTimeout(refreshTimer);
    refreshTimer = setTimeout(() => { refreshGate().catch(() => {}); }, 350);
  }

  function checkSite() {
    chrome.runtime.sendMessage({ type: 'CHECK_ORIGIN', origin, pageUrl: location.href }, (response) => {
      if (chrome.runtime.lastError || !response?.matched) {
        site = undefined;
        mobileApproved = false;
        removeGate();
        return;
      }
      site = response.site;
      mobileApproved = response.approved === true;
      signedIn = Boolean(response.signedIn);
      scheduleRefresh();
    });
  }

  function onSpaNavigation() {
    scheduleRefresh();
    checkSite();
  }

  if (pageState().isSignedOut) {
    site = { name: location.hostname, isVerified: true };
    ensureGate();
    reportStatus('Checking whether this site is registered with Safe Passage…');
  }
  checkSite();
  new MutationObserver((records) => {
    for (const record of records) {
      for (const node of record.addedNodes) {
        if (node.nodeType === Node.ELEMENT_NODE && node.parentElement === document.body && node !== gateHost && gateHost) {
          const previous = { inert: node.inert, hadMarker: node.hasAttribute('data-safe-passage-inert') };
          node.inert = true;
          node.setAttribute('data-safe-passage-inert', '');
          inertNodes.set(node, previous);
        }
      }
    }
    scheduleRefresh();
  }).observe(document.documentElement, { childList: true, subtree: true, attributes: true, characterData: true,
    attributeFilter: ['type', 'style', 'hidden', 'aria-label'] });
  window.addEventListener('popstate', onSpaNavigation);
  window.addEventListener('hashchange', onSpaNavigation);
  for (const method of ['pushState', 'replaceState']) {
    const original = history[method].bind(history);
    history[method] = (...args) => { const result = original(...args); onSpaNavigation(); return result; };
  }

  chrome.runtime.onMessage.addListener((message) => {
    if (message.type === 'APPROVAL_PENDING') reportStatus('Approval request sent. Open Safe Passage on your trusted device.');
    if (message.type === 'SITE_ACCESS_RETRY' || message.type === 'START_SITE_GATE') checkSite();
    if (message.type === 'AUTOFILL_NOW') {
      mobileApproved = true;
      fillCredentials(message);
      submitCurrentAuthStep();
      scheduleRefresh();
    }
  });
})();
