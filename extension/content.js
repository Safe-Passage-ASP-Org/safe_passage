(function () {
  const BANNER_ID = 'safepassage-ext-prompt';

  function findLoginInputs() {
    const passwordInput = document.querySelector('input[type="password"]:not([disabled])');
    if (!passwordInput) return null;

    const form = passwordInput.closest('form') || document;
    const userInput =
      form.querySelector('input[autocomplete="username"], input[type="email"], input[name*="user" i], input[name*="email" i], input[type="text"]');

    return { userInput, passwordInput };
  }

  function setInputValue(el, value) {
    if (!el || value == null) return;
    const proto = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value');
    if (proto && proto.set) {
      proto.set.call(el, value);
    } else {
      el.value = value;
    }
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
  }

  function renderPrompt(siteName, signedIn) {
    if (document.getElementById(BANNER_ID)) return;

    const box = document.createElement('div');
    box.id = BANNER_ID;
    box.style.cssText = [
      'position:fixed',
      'bottom:20px',
      'right:20px',
      'z-index:2147483647',
      'background:#171a1e',
      'color:#e6e8eb',
      'border:1px solid #2e343b',
      'border-radius:10px',
      'padding:14px 16px',
      'width:300px',
      'font-family:Inter,-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif',
      'font-size:13px',
      'box-shadow:0 12px 32px rgba(0,0,0,0.45)',
    ].join(';');

    box.innerHTML = `
      <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:6px;">
        <strong style="font-size:13px;">Safe Passage</strong>
        <button id="sp-ext-close" type="button" style="background:none;border:none;color:#939aa3;cursor:pointer;font-size:15px;">x</button>
      </div>
      <div id="sp-ext-msg" style="color:#939aa3;margin-bottom:10px;">
        ${signedIn ? `Authenticate <strong>${siteName}</strong> with Safe Passage to autofill.` : 'Open the Safe Passage extension icon to sign in first.'}
      </div>
      <button id="sp-ext-auth" type="button" style="width:100%;padding:8px 12px;border-radius:7px;border:1px solid #3b82f6;background:#1d4ed8;color:#fff;font-weight:600;cursor:pointer;">
        Authenticate &amp; Autofill
      </button>
    `;

    document.body.appendChild(box);

    document.getElementById('sp-ext-close').addEventListener('click', () => box.remove());
    document.getElementById('sp-ext-auth').addEventListener('click', async () => {
      const msgEl = document.getElementById('sp-ext-msg');
      const btnEl = document.getElementById('sp-ext-auth');
      btnEl.disabled = true;
      msgEl.textContent = 'Verifying Safe Passage session and MFA...';

      chrome.runtime.sendMessage(
        { type: 'AUTHENTICATE_AND_GET_CREDENTIALS', origin: window.location.origin },
        (res) => {
          btnEl.disabled = false;
          if (!res || !res.ok) {
            msgEl.style.color = '#f87171';
            msgEl.textContent = res?.error || 'Authentication failed.';
            return;
          }
          const inputs = findLoginInputs();
          if (!inputs) {
            msgEl.style.color = '#fbbf24';
            msgEl.textContent = 'Approved, but no login form fields were found on this page.';
            return;
          }
          setInputValue(inputs.userInput, res.username);
          setInputValue(inputs.passwordInput, res.password);
          msgEl.style.color = '#4ade80';
          msgEl.textContent = 'Authenticated and autofilled.';
          setTimeout(() => box.remove(), 1800);
        }
      );
    });
  }

  chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
    if (msg.type === 'AUTOFILL_NOW') {
      const inputs = findLoginInputs();
      if (!inputs) {
        sendResponse({ ok: false, error: 'No login form found on this page.' });
        return;
      }
      setInputValue(inputs.userInput, msg.username);
      setInputValue(inputs.passwordInput, msg.password);
      sendResponse({ ok: true });
    }
  });

  function init() {
    if (!findLoginInputs()) return;
    chrome.runtime.sendMessage({ type: 'CHECK_ORIGIN', origin: window.location.origin }, (res) => {
      if (res?.ok && res.matched) {
        renderPrompt(res.site.name, res.signedIn);
      }
    });
  }

  init();
})();
