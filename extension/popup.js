function send(msg) {
  return new Promise((resolve) => chrome.runtime.sendMessage(msg, resolve));
}

async function getActiveTab() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  return tab;
}

async function refresh() {
  const statusMsg = document.getElementById('statusMsg');
  const loginView = document.getElementById('loginView');
  const signedInView = document.getElementById('signedInView');

  const res = await send({ type: 'GET_STATE' });
  if (!res?.ok) {
    statusMsg.textContent = res?.error || 'Error loading state';
    return;
  }

  document.getElementById('apiUrl').value = res.apiUrl;

  if (!res.signedIn) {
    statusMsg.textContent = 'Not signed in.';
    loginView.classList.remove('hidden');
    signedInView.classList.add('hidden');
    return;
  }

  statusMsg.textContent = `Connected to ${res.apiUrl}`;
  loginView.classList.add('hidden');
  signedInView.classList.remove('hidden');
  document.getElementById('userLabel').textContent = res.username || 'Signed in';

  const tab = await getActiveTab();
  if (tab?.url && tab.url.startsWith('http')) {
    const parsed = new URL(tab.url);
    document.getElementById('siteOrigin').value = parsed.origin;
    document.getElementById('currentSiteLabel').textContent = parsed.hostname;
    if (!document.getElementById('siteName').value) {
      document.getElementById('siteName').value = parsed.hostname;
    }
  } else {
    document.getElementById('currentSiteLabel').textContent = 'Open an http/https website to continue';
  }

  const list = document.getElementById('sitesList');
  if (!res.customSites.length) {
    list.innerHTML = '<div class="status">No custom sites saved yet.</div>';
  } else {
    list.innerHTML = '';
    for (const s of res.customSites) {
      const row = document.createElement('div');
      row.className = 'site-row';
      const info = document.createElement('div');
      info.className = 'site-info';
      const name = document.createElement('strong');
      name.textContent = s.name;
      const details = document.createElement('span');
      details.style.cssText = 'display:block;color:#939aa3;font-size:11px;';
      details.textContent = [s.url || s.origin, s.category, s.verificationStatus, s.hasTotpSecret ? '2FA saved' : ''].filter(Boolean).join(' · ');
      info.append(name, details);
      const del = document.createElement('button');
      del.className = 'danger';
      del.textContent = 'Remove';
      del.addEventListener('click', async () => {
        await send({ type: 'DELETE_CUSTOM_SITE', id: s.id });
        refresh();
      });
      row.append(info, del);
      list.appendChild(row);
    }
  }
}

document.getElementById('loginBtn').addEventListener('click', async () => {
  const btn = document.getElementById('loginBtn');
  const statusMsg = document.getElementById('statusMsg');
  btn.disabled = true;
  statusMsg.textContent = 'Signing in (waiting for MFA if required)...';

  const res = await send({
    type: 'LOGIN',
    apiUrl: document.getElementById('apiUrl').value.trim(),
    username: document.getElementById('loginUser').value.trim(),
    password: document.getElementById('loginPass').value,
  });

  btn.disabled = false;
  if (!res?.ok) {
    statusMsg.textContent = res?.error || 'Login failed';
    return;
  }
  refresh();
  const tab = await getActiveTab();
  if (tab?.id && tab.url?.startsWith('http')) {
    chrome.tabs.sendMessage(tab.id, { type: 'SITE_ACCESS_RETRY', signedIn: true }, () => void chrome.runtime.lastError);
  }
});

document.getElementById('logoutBtn').addEventListener('click', async () => {
  await send({ type: 'LOGOUT' });
  refresh();
});

document.getElementById('saveSiteBtn').addEventListener('click', async () => {
  const statusMsg = document.getElementById('statusMsg');
  const name = document.getElementById('siteName').value.trim();
  const url = document.getElementById('siteOrigin').value.trim();
  if (!name || !url) {
    statusMsg.textContent = 'Enter a label and login/web URL for this site.';
    return;
  }
  const res = await send({
    type: 'SAVE_CUSTOM_SITE',
    name,
    url,
    category: document.getElementById('siteCategory').value,
    description: document.getElementById('siteDescription').value.trim(),
    siteUsername: document.getElementById('siteUser').value.trim(),
    sitePassword: document.getElementById('sitePass').value,
    totpSecret: document.getElementById('siteTotpSecret').value,
  });
  document.getElementById('sitePass').value = '';
  document.getElementById('siteTotpSecret').value = '';
  if (res?.ok) {
    const tab = await getActiveTab();
    if (tab?.id && tab.url?.startsWith('http') && new URL(tab.url).origin === res.origin) {
      chrome.tabs.sendMessage(tab.id, { type: 'START_SITE_GATE', signedIn: true, siteName: document.getElementById('siteName').value.trim() }, () => void chrome.runtime.lastError);
    }
  }
  await refresh();
  statusMsg.textContent = res?.ok
    ? `Saved ${name}. Site verification status: ${res.verificationStatus || 'Pending'}.`
    : (res?.error || 'Failed to save site');
});

document.getElementById('autofillTabBtn').addEventListener('click', async () => {
  const statusMsg = document.getElementById('statusMsg');
  const tab = await getActiveTab();
  if (!tab?.url || !tab.url.startsWith('http')) {
    statusMsg.textContent = 'Open an http/https site first.';
    return;
  }
  const origin = new URL(tab.url).origin;
  statusMsg.textContent = 'Verifying Safe Passage access...';
  const creds = await send({ type: 'AUTHENTICATE_AND_GET_CREDENTIALS', origin });
  if (!creds?.ok) {
    statusMsg.textContent = creds?.error || 'Access denied';
    return;
  }
  chrome.tabs.sendMessage(tab.id, { type: 'AUTOFILL_NOW', username: creds.username, password: creds.password, totpCode: creds.totpCode }, (fillRes) => {
    statusMsg.textContent = fillRes?.ok ? 'Autofilled current tab.' : (fillRes?.error || 'Could not autofill page.');
  });
});

refresh();

document.getElementById('togglePassword').addEventListener('click', () => {
  const input = document.getElementById('sitePass');
  const button = document.getElementById('togglePassword');
  const reveal = input.type === 'password';
  input.type = reveal ? 'text' : 'password';
  button.textContent = reveal ? 'Hide' : 'Show';
  button.setAttribute('aria-label', reveal ? 'Hide password' : 'Show password');
});
