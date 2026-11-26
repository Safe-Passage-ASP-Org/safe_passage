import { decryptSecret } from './crypto.js';

const DEFAULT_API_URL = 'https://safe-passage-api.onrender.com';
const pendingApprovals = new Map();
const approvalFlows = new Map();
let assignedAppsCache = [];
let assignedAppsCacheToken = '';
let assignedAppsCacheAt = 0;

chrome.sidePanel.setPanelBehavior({ openPanelOnActionClick: true }).catch(() => {});

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

async function getVaultSites(apiUrl, accessToken) {
  const res = await fetch(`${apiUrl}/api/vault/sites`, { headers: { 'Authorization': `Bearer ${accessToken}` } });
  if (res.status === 401) {
    await chrome.storage.local.remove('accessToken');
    throw new Error('Safe Passage session expired. Sign in again from the extension menu.');
  }
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error(body.detail || body.title || `Could not sync the Safe Passage vault (${res.status}).`);
  }
  const sites = await res.json();
  await chrome.storage.local.set({ vaultSitesCache: sites });
  return sites;
}

async function migrateLocalSites(state) {
  if (!state.accessToken || state.customSites.length === 0) return;
  let remoteSites;
  try { remoteSites = await getVaultSites(state.apiUrl, state.accessToken); }
  catch { return; }
  const remaining = [...state.customSites];
  for (const localSite of state.customSites) {
    const origin = localSite.origin;
    let password = '';
    try { password = await decryptSecret(localSite.encryptedPassword); } catch { continue; }
    const remoteSite = remoteSites.find((site) => site.origin === origin);
    const response = await fetch(`${state.apiUrl}/api/vault/sites${remoteSite ? `/${encodeURIComponent(remoteSite.id)}` : ''}`, {
      method: remoteSite ? 'PUT' : 'POST',
      headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${state.accessToken}` },
      body: JSON.stringify({
        name: localSite.name, loginUrl: localSite.url || origin, category: localSite.category || 'Other',
        description: localSite.description || '', autoFillUsername: localSite.siteUsername || '',
        password, totpSecret: '',
      }),
    });
    if (!response.ok) continue;
    const index = remaining.findIndex((site) => site.id === localSite.id);
    if (index >= 0) remaining.splice(index, 1);
  }
  await chrome.storage.local.set({ customSites: remaining });
}

function findAssignedApplication(applications, origin) {
  return applications.find((app) => {
    try { return new URL(app.launchUrl).origin === origin; }
    catch { return false; }
  }) || null;
}

async function getSiteForOrigin(state, origin) {
  if (state.accessToken) await migrateLocalSites(state);
  const cached = await chrome.storage.local.get('vaultSitesCache');
  let vaultSites = Array.isArray(cached.vaultSitesCache) ? cached.vaultSitesCache : [];
  if (state.accessToken) vaultSites = await getVaultSites(state.apiUrl, state.accessToken);
  const vaultSite = vaultSites.find((site) => site.origin === origin) || null;
  if (vaultSite) {
    return {
      id: vaultSite.id, name: vaultSite.name, origin, vaultSiteId: vaultSite.id,
      vaultSite: true, isVerified: vaultSite.verificationStatus === 'Verified',
      lockedByPlan: vaultSite.lockedByPlan === true,
      canGenerateTotpCode: vaultSite.canGenerateTotpCode === true,
      verificationStatus: vaultSite.verificationStatus,
      siteUsername: vaultSite.autoFillUsername || '', hasAutoFillPassword: vaultSite.hasPassword,
    };
  }
  try {
    const apps = await getAssignedApplications(state.apiUrl, state.accessToken);
    const app = findAssignedApplication(apps, origin);
    if (app) {
      return {
        id: app.id,
        name: app.name,
        origin,
        applicationId: app.id,
        siteUsername: app.autoFillUsername || '',
        remoteCredentials: true,
        isVerified: true,
        verificationStatus: 'Verified',
        hasAutoFillPassword: app.hasAutoFillPassword,
      };
    }
  } catch (error) {
    throw error;
  }
  return null;
}

async function getRegisteredApplicationCredentials(apiUrl, approvedToken, applicationId) {
  const res = await fetch(`${apiUrl}/api/integrations/access/${encodeURIComponent(applicationId)}/credentials`, {
    headers: { 'Authorization': `Bearer ${approvedToken}` },
  });
  const body = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(body.detail || body.title || `Could not retrieve application credentials (${res.status})`);
  return { username: body.username || '', password: body.password || '' };
}

async function getVaultSiteCredentials(apiUrl, approvedToken, vaultSiteId) {
  const res = await fetch(`${apiUrl}/api/vault/sites/${encodeURIComponent(vaultSiteId)}/credentials`, {
    headers: { 'Authorization': `Bearer ${approvedToken}` },
  });
  const body = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(body.detail || body.title || `Could not retrieve saved credentials (${res.status})`);
  return { username: body.username || '', password: body.password || '', totpCode: body.totpCode || '', requiresProForTotp: body.requiresProForTotp === true };
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
      let sites = [];
      if (state.accessToken) {
        try { await migrateLocalSites(state); sites = await getVaultSites(state.apiUrl, state.accessToken); }
        catch { sites = []; }
      } else {
        const cached = await chrome.storage.local.get('vaultSitesCache');
        sites = Array.isArray(cached.vaultSitesCache) ? cached.vaultSitesCache : [];
      }
      return {
        apiUrl: state.apiUrl,
        signedIn: Boolean(state.accessToken),
        username: state.username,
        customSites: sites.map(site => ({
          id: site.id, name: site.name, origin: site.origin,
          url: site.loginUrl || site.origin, category: site.category || '',
          description: site.description || '', siteUsername: site.autoFillUsername || '',
          verificationStatus: site.verificationStatus,
          hasTotpSecret: site.hasTotpSecret,
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
      await chrome.storage.session.clear();
      return { ok: true };
    }

    if (msg.type === 'SAVE_CUSTOM_SITE') {
      if (!state.accessToken) throw new Error('Sign in to Safe Passage before saving this site to your synchronized vault.');
      const applicationUrl = new URL(msg.url);
      if (!msg.name?.trim()) throw new Error('Site name is required.');
      const response = await fetch(`${state.apiUrl}/api/vault/sites`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${state.accessToken}` },
        body: JSON.stringify({ name: msg.name.trim(), loginUrl: applicationUrl.href, category: msg.category || 'Other',
          description: (msg.description || '').trim(), autoFillUsername: msg.siteUsername || '',
          password: msg.sitePassword || '', totpSecret: msg.totpSecret || '' }),
      });
      const body = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(body.detail || body.title || body.message || `Could not save site (${response.status})`);
      await getVaultSites(state.apiUrl, state.accessToken);
      return { ok: true, id: body.id, origin: body.origin, verificationStatus: body.verificationStatus };
    }

    if (msg.type === 'DELETE_CUSTOM_SITE') {
      if (!state.accessToken) throw new Error('Sign in to Safe Passage before removing a synchronized site.');
      const response = await fetch(`${state.apiUrl}/api/vault/sites/${encodeURIComponent(msg.id)}`, {
        method: 'DELETE', headers: { 'Authorization': `Bearer ${state.accessToken}` },
      });
      if (!response.ok && response.status !== 404) throw new Error(`Could not remove saved site (${response.status})`);
      await getVaultSites(state.apiUrl, state.accessToken);
      return { ok: true };
    }

    if (msg.type === 'CHECK_ORIGIN') {
      const site = await getSiteForOrigin(state, msg.origin);
      if (!site) return { matched: false };
      const sessionKey = `approval:${site.origin}`;
      const approval = (await chrome.storage.session.get(sessionKey))[sessionKey];
      return {
        matched: true,
        signedIn: Boolean(state.accessToken),
        approved: Boolean(approval?.token && approval.expiresAt > Date.now()),
        site: { id: site.id, name: site.name, origin: site.origin, applicationId: site.applicationId || null,
          vaultSiteId: site.vaultSiteId || null, isVerified: site.isVerified !== false,
          verificationStatus: site.verificationStatus || 'Verified', lockedByPlan: site.lockedByPlan === true },
      };
    }

    if (msg.type === 'SITE_GATE_STATUS') {
      if (sender.tab?.id != null) {
        chrome.runtime.sendMessage({
          type: 'SITE_GATE_STATUS_UPDATE', tabId: sender.tab.id,
          origin: msg.origin, status: msg.status, siteName: msg.siteName || '',
        }, () => void chrome.runtime.lastError);
      }
      return { ok: true };
    }

    if (msg.type === 'AUTHENTICATE_AND_GET_CREDENTIALS') {
      if (!state.accessToken) {
        throw new Error('Sign in via the Safe Passage extension popup first.');
      }
      const site = await getSiteForOrigin(state, msg.origin);
      if (!site) throw new Error('Site is not registered in Safe Passage.');
      if (!site.isVerified) throw new Error('This site is awaiting Safe Passage verification before credentials or 2FA can be used.');
      if (site.lockedByPlan) throw new Error('This saved website is over the Free plan limit. Upgrade to Pro or remove another saved website.');

      const approvalKey = `approval:${site.origin}`;
      const existingApproval = (await chrome.storage.session.get(approvalKey))[approvalKey];
      let approvedToken;
      if (existingApproval?.token && existingApproval.expiresAt > Date.now()) {
        approvedToken = existingApproval.token;
      } else if (!approvalFlows.has(site.origin)) {
        const approvalFlow = requestSiteApproval(state.apiUrl, state.accessToken, site.origin, sender.tab?.id);
        approvalFlows.set(site.origin, approvalFlow);
        try {
          approvedToken = await approvalFlow;
        } finally {
          approvalFlows.delete(site.origin);
        }
      } else {
        approvedToken = await approvalFlows.get(site.origin);
      }

      let username = site.siteUsername || '';
      let password = '';
      let totpCode = '';
      let requiresProForTotp = false;
      await chrome.storage.session.set({ [approvalKey]: {
        token: approvedToken, expiresAt: Date.now() + 5 * 60 * 1000,
        vaultSiteId: site.vaultSiteId || null, applicationId: site.applicationId || null,
      } });
      if (site.vaultSite && site.vaultSiteId) {
        ({ username, password, totpCode, requiresProForTotp } = await getVaultSiteCredentials(state.apiUrl, approvedToken, site.vaultSiteId));
      } else if (site.remoteCredentials && site.applicationId) {
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
        totpCode,
        requiresProForTotp,
      };
    }

    if (msg.type === 'GET_CURRENT_TOTP') {
      const site = await getSiteForOrigin(state, msg.origin);
      if (!site?.isVerified || !site.vaultSiteId) throw new Error('No verified 2FA account is saved for this site.');
      const sessionKey = `approval:${site.origin}`;
      const approval = (await chrome.storage.session.get(sessionKey))[sessionKey];
      if (!approval?.token || approval.expiresAt <= Date.now()) throw new Error('Request mobile approval before using this 2FA code.');
      const credentials = await getVaultSiteCredentials(state.apiUrl, approval.token, site.vaultSiteId);
      if (credentials.requiresProForTotp) throw new Error('Authenticator codes require an active Pro subscription.');
      if (!credentials.totpCode) throw new Error('No 2FA secret is saved for this site.');
      return { ok: true, totpCode: credentials.totpCode };
    }

    if (msg.type === 'GET_APPROVED_CREDENTIALS') {
      const site = await getSiteForOrigin(state, msg.origin);
      if (!site?.isVerified) throw new Error('This site is not verified.');
      const sessionKey = `approval:${site.origin}`;
      const approval = (await chrome.storage.session.get(sessionKey))[sessionKey];
      if (!approval?.token || approval.expiresAt <= Date.now()) throw new Error('Request mobile approval before using saved credentials.');
      if (site.vaultSite && site.vaultSiteId)
        return { ok: true, ...await getVaultSiteCredentials(state.apiUrl, approval.token, site.vaultSiteId) };
      if (site.applicationId) {
        const result = await verifyAccess(state.apiUrl, approval.token, site.applicationId);
        if (result?.allowed === false) throw new Error(`Access denied by Safe Passage (${result.reason || 'policy'}).`);
        return { ok: true, ...await getRegisteredApplicationCredentials(state.apiUrl, approval.token, site.applicationId) };
      }
      throw new Error('No saved credentials are available for this site.');
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
