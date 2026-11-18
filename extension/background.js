import { encryptSecret, decryptSecret } from './crypto.js';

const DEFAULT_API_URL = 'https://safe-passage-api.onrender.com';
const pendingApprovals = new Map();
const approvalFlows = new Map();
let assignedAppsCache = [];
let assignedAppsCacheToken = '';
let assignedAppsCacheAt = 0;

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
  const deadline = Date.now() + 5 * 60 * 1000;
  while (Date.now() < deadline) {
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

async function requestSiteApproval(apiUrl, accessToken, origin, tabId) {
  const res = await fetch(`${apiUrl}/api/integrations/access/request-approval`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'Authorization': `Bearer ${accessToken}`,
    },
    body: JSON.stringify({ origin }),
  });
  const body = await res.json().catch(() => ({}));
  if (!res.ok) {
    if (res.status === 401) {
      await chrome.storage.local.remove('accessToken');
      throw new Error('Safe Passage session expired. Sign in again from the extension menu.');
    }
    throw new Error(body.detail || body.title || `Could not request approval (${res.status})`);
  }
  if (!body.mfaRequired || !body.sessionToken) {
    throw new Error('Safe Passage did not create an approval request.');
  }

  pendingApprovals.set(origin, body.sessionToken);
  if (Number.isInteger(tabId)) {
    chrome.tabs.sendMessage(tabId, { type: 'APPROVAL_PENDING' }, () => void chrome.runtime.lastError);
  }
  try {
    return await waitForMfaApproval(apiUrl, body.sessionToken);
  } finally {
    pendingApprovals.delete(origin);
  }
}

async function getAssignedApplications(apiUrl, accessToken) {
  if (!accessToken) return [];
  if (assignedAppsCacheToken === accessToken && Date.now() - assignedAppsCacheAt < 60_000)
    return assignedAppsCache;

  const res = await fetch(`${apiUrl}/api/applications/mine`, {
    headers: { 'Authorization': `Bearer ${accessToken}` },
  });
  if (res.status === 401) {
    await chrome.storage.local.remove('accessToken');
    assignedAppsCache = [];
    assignedAppsCacheToken = '';
    throw new Error('Safe Passage session expired. Sign in again from the extension menu.');
  }
  if (!res.ok) return [];
  assignedAppsCache = await res.json();
  assignedAppsCacheToken = accessToken;
  assignedAppsCacheAt = Date.now();
  return assignedAppsCache;
}

function findAssignedApplication(applications, origin) {
  return applications.find((app) => {
    try { return new URL(app.launchUrl).origin === origin; }
    catch { return false; }
  }) || null;
}

async function getSiteForOrigin(state, origin) {
  const localSite = state.customSites.find((site) => site.origin === origin) || null;
  if (!state.accessToken) return localSite;
  const apps = await getAssignedApplications(state.apiUrl, state.accessToken);
  const app = findAssignedApplication(apps, origin);
  if (app) {
    return {
      ...(localSite || {}),
      id: localSite?.id || app.id,
      name: app.name,
      origin,
      applicationId: app.id,
      siteUsername: app.autoFillUsername || '',
      remoteCredentials: true,
      hasAutoFillPassword: app.hasAutoFillPassword,
    };
  }
  return localSite;
}

async function getRegisteredApplicationCredentials(apiUrl, approvedToken, applicationId) {
  const res = await fetch(`${apiUrl}/api/integrations/access/${encodeURIComponent(applicationId)}/credentials`, {
    headers: { 'Authorization': `Bearer ${approvedToken}` },
  });
  const body = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(body.detail || body.title || `Could not retrieve application credentials (${res.status})`);
  return { username: body.username || '', password: body.password || '' };
}

async function resendSiteApproval(apiUrl, origin) {
  const sessionToken = pendingApprovals.get(origin);
  if (!sessionToken) throw new Error('There is no pending approval request for this site.');
  const state = await getState();
  const res = await fetch(`${apiUrl}/api/auth/mfa/resend`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'Authorization': `Bearer ${state.accessToken}`,
    },
    body: JSON.stringify({ sessionToken }),
  });
  const body = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(body.detail || body.title || `Could not resend approval (${res.status})`);
  return { message: body.message || 'Approval request resent.' };
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
        customSites: state.customSites.map(({ id, name, origin, url, category, description, siteUsername }) => ({
          id,
          name,
          origin,
          url: url || origin,
          category: category || '',
          description: description || '',
          siteUsername,
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
      const applicationUrl = new URL(msg.url);
      if (!['http:', 'https:'].includes(applicationUrl.protocol)) throw new Error('Login/Web URL must use HTTP or HTTPS.');
      if (!msg.name?.trim() || !msg.category) throw new Error('Application name and category are required.');
      const origin = applicationUrl.origin;
      const encryptedPassword = await encryptSecret(msg.sitePassword || '');
      const next = state.customSites.filter((s) => s.origin !== origin);
      next.push({
        id: crypto.randomUUID(),
        name: msg.name.trim(),
        origin,
        url: applicationUrl.href,
        category: msg.category,
        description: (msg.description || '').trim(),
        siteUsername: msg.siteUsername || '',
        encryptedPassword,
        applicationId: null,
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
      const site = await getSiteForOrigin(state, msg.origin);
      if (!site) return { matched: false };
      return {
        matched: true,
        signedIn: Boolean(state.accessToken),
        site: { id: site.id, name: site.name, origin: site.origin, applicationId: site.applicationId || null },
      };
    }

    if (msg.type === 'AUTHENTICATE_AND_GET_CREDENTIALS') {
      if (!state.accessToken) {
        throw new Error('Sign in via the Safe Passage extension popup first.');
      }
      const site = await getSiteForOrigin(state, msg.origin);
      if (!site) throw new Error('Site is not registered in Safe Passage.');

      if (!approvalFlows.has(site.origin)) {
        const approvalFlow = requestSiteApproval(state.apiUrl, state.accessToken, site.origin, sender.tab?.id);
        approvalFlows.set(site.origin, approvalFlow);
        try {
          var approvedToken = await approvalFlow;
        } finally {
          approvalFlows.delete(site.origin);
        }
      } else {
        var approvedToken = await approvalFlows.get(site.origin);
      }

      let username = site.siteUsername || '';
      let password = '';
      if (site.remoteCredentials && site.applicationId) {
        const verification = await verifyAccess(state.apiUrl, approvedToken, site.applicationId);
        if (verification && verification.allowed === false)
          throw new Error(`Access denied by Safe Passage (${verification.reason || 'policy'}).`);
        ({ username, password } = await getRegisteredApplicationCredentials(state.apiUrl, approvedToken, site.applicationId));
      } else {
        if (site.applicationId) {
          const verification = await verifyAccess(state.apiUrl, approvedToken, site.applicationId);
          if (verification && verification.allowed === false)
            throw new Error(`Access denied by Safe Passage (${verification.reason || 'policy'}).`);
        }
        password = await decryptSecret(site.encryptedPassword);
      }
      return {
        ok: true,
        username,
        password,
      };
    }

    if (msg.type === 'RESEND_SITE_APPROVAL') {
      return await resendSiteApproval(state.apiUrl, msg.origin);
    }

    return { error: 'Unknown message type' };
  })()
    .then((result) => sendResponse({ ok: true, ...result }))
    .catch((err) => sendResponse({ ok: false, error: err.message }));

  return true;
});
