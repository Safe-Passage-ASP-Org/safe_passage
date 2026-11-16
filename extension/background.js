import { encryptSecret, decryptSecret } from './crypto.js';

const DEFAULT_API_URL = 'https://safe-passage-api.onrender.com';

async function getState() {
  const data = await chrome.storage.local.get([
    'apiUrl',
    'accessToken',
    'username',
    'customSites',
  ]);
  return {
    apiUrl: (data.apiUrl || DEFAULT_API_URL).replace(/\/+$/, ''),
    accessToken: data.accessToken || '',
    username: data.username || '',
    customSites: Array.isArray(data.customSites) ? data.customSites : [],
  };
}

async function waitForMfaApproval(apiUrl, sessionToken) {
  for (let i = 0; i < 45; i++) {
    const res = await fetch(`${apiUrl}/api/auth/mfa/status/${encodeURIComponent(sessionToken)}`);
    if (!res.ok) {
      const err = await res.json().catch(() => ({}));
      throw new Error(err.detail || err.title || `MFA check failed (${res.status})`);
    }
    const body = await res.json();
    if (body.status === 'Approved' && body.accessToken) {
      return body.accessToken;
    }
    if (body.status === 'Denied' || body.status === 'Expired') {
      throw new Error(`MFA ${body.status.toLowerCase()}.`);
    }
    await new Promise((r) => setTimeout(r, 2000));
  }
  throw new Error('MFA approval timed out.');
}

async function loginToSafePassage({ apiUrl, username, password, deviceId }) {
  const base = (apiUrl || DEFAULT_API_URL).replace(/\/+$/, '');
  const res = await fetch(`${base}/api/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password, deviceId: deviceId || undefined }),
  });

  const body = await res.json().catch(() => ({}));
  if (!res.ok && res.status !== 202) {
    throw new Error(body.detail || body.title || `Login failed (${res.status})`);
  }

  let token = body.accessToken || '';
  if (body.mfaRequired && body.sessionToken) {
    token = await waitForMfaApproval(base, body.sessionToken);
  }

  if (!token) throw new Error('No access token returned by Safe Passage API.');

  await chrome.storage.local.set({
    apiUrl: base,
    accessToken: token,
    username,
  });

  return { ok: true, username };
}

async function verifyAccess(apiUrl, accessToken, applicationId) {
  if (!applicationId) return { allowed: true, reason: 'custom_site_session' };
  const res = await fetch(`${apiUrl}/api/integrations/access/verify`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'Authorization': `Bearer ${accessToken}`,
    },
    body: JSON.stringify({ applicationId }),
  });
  if (res.status === 401) {
    await chrome.storage.local.remove('accessToken');
    throw new Error('Safe Passage session expired. Please sign in again.');
  }
  if (!res.ok) {
    throw new Error(`Access verification failed (${res.status})`);
  }
  return res.json();
}

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  (async () => {
    const state = await getState();

    if (msg.type === 'GET_STATE') {
      return {
        apiUrl: state.apiUrl,
        signedIn: Boolean(state.accessToken),
        username: state.username,
        customSites: state.customSites.map(({ id, name, origin, siteUsername, applicationId }) => ({
          id,
          name,
          origin,
          siteUsername,
          applicationId: applicationId || null,
        })),
      };
    }

    if (msg.type === 'SET_API_URL') {
      const apiUrl = (msg.apiUrl || DEFAULT_API_URL).replace(/\/+$/, '');
      await chrome.storage.local.set({ apiUrl });
      return { ok: true, apiUrl };
    }

    if (msg.type === 'LOGIN') {
      return await loginToSafePassage(msg);
    }

    if (msg.type === 'LOGOUT') {
      await chrome.storage.local.remove(['accessToken', 'username']);
      return { ok: true };
    }

    if (msg.type === 'SAVE_CUSTOM_SITE') {
      const origin = new URL(msg.url).origin;
      const encryptedPassword = await encryptSecret(msg.sitePassword || '');
      const next = state.customSites.filter((s) => s.origin !== origin);
      next.push({
        id: crypto.randomUUID(),
        name: msg.name || origin,
        origin,
        siteUsername: msg.siteUsername || '',
        encryptedPassword,
        applicationId: msg.applicationId || null,
      });
      await chrome.storage.local.set({ customSites: next });
      return { ok: true, origin };
    }

    if (msg.type === 'DELETE_CUSTOM_SITE') {
      const next = state.customSites.filter((s) => s.id !== msg.id);
      await chrome.storage.local.set({ customSites: next });
      return { ok: true };
    }

    if (msg.type === 'CHECK_ORIGIN') {
      const site = state.customSites.find((s) => s.origin === msg.origin);
      if (!site) return { matched: false };
      return {
        matched: true,
        signedIn: Boolean(state.accessToken),
        site: { id: site.id, name: site.name, origin: site.origin },
      };
    }

    if (msg.type === 'AUTHENTICATE_AND_GET_CREDENTIALS') {
      if (!state.accessToken) {
        throw new Error('Sign in via the Safe Passage extension popup first.');
      }
      const site = state.customSites.find((s) => s.origin === msg.origin);
      if (!site) throw new Error('Site is not registered in Safe Passage.');

      const verification = await verifyAccess(state.apiUrl, state.accessToken, site.applicationId);
      if (verification && verification.allowed === false) {
        throw new Error(`Access denied by Safe Passage (${verification.reason || 'policy'}).`);
      }

      const sitePassword = await decryptSecret(site.encryptedPassword);
      return {
        ok: true,
        username: site.siteUsername,
        password: sitePassword,
      };
    }

    return { error: 'Unknown message type' };
  })()
    .then((result) => sendResponse({ ok: true, ...result }))
    .catch((err) => sendResponse({ ok: false, error: err.message }));

  return true;
});
