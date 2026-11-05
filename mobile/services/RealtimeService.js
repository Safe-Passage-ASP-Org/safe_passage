import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { BASE_URL } from '../config';
import { getToken } from './AuthService';
import { getApprovalDeviceId } from './DeviceSigningService';

let connection;
let activeDeviceId;
let activeUserId;
let requestHandler;
let retryTimer;
let stopped = false;

export function setMfaRequestHandler(handler) {
  requestHandler = handler;
}

export async function startMfaRealtime() {
  stopped = false;
  const token = await getToken();
  const deviceId = await getApprovalDeviceId();
  const userId = getUserIdFromToken(token);
  if (!token || !deviceId || !userId) return false;

  if (connection
    && activeDeviceId === deviceId
    && activeUserId === userId
    && connection.state === 'Connected') return true;
  await stopMfaRealtime();
  stopped = false;
  activeDeviceId = deviceId;
  activeUserId = userId;
  const connectionUserId = userId;

  const current = new HubConnectionBuilder()
    .withUrl(`${BASE_URL}/mfaHub`, {
      accessTokenFactory: async () => {
        const currentToken = await getToken();
        return getUserIdFromToken(currentToken) === connectionUserId ? currentToken : '';
      },
    })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();

  current.on('MfaRequested', payload => {
    if (!isMfaRequestForAccount(payload, connectionUserId, deviceId)) return;
    requestHandler?.({
      requestId: payload.sessionToken,
      deviceId: payload.deviceId,
      time: payload.requestedAt ? new Date(payload.requestedAt).toLocaleTimeString() : 'Just now',
    });
  });
  current.onreconnected(async () => {
    try {
      await current.invoke('JoinDeviceGroup', activeDeviceId);
    } catch (error) {
      console.warn('[MFA] Could not rejoin the trusted-device notification group:', error.message);
    }
  });
  current.onclose(() => {
    connection = null;
    if (!stopped) scheduleRetry();
  });

  connection = current;
  try {
    await current.start();
    await current.invoke('JoinDeviceGroup', deviceId);
    return true;
  } catch (error) {
    console.warn('[MFA] Real-time connection failed:', error.message);
    try { await current.stop(); } catch {}
    if (connection === current) connection = null;
    if (!stopped) scheduleRetry();
    return false;
  }
}

export async function stopMfaRealtime() {
  stopped = true;
  if (retryTimer) {
    clearTimeout(retryTimer);
    retryTimer = null;
  }
  const current = connection;
  connection = null;
  activeDeviceId = null;
  activeUserId = null;
  if (current) {
    try { await current.stop(); } catch {}
  }
}

function scheduleRetry() {
  if (retryTimer || stopped) return;
  retryTimer = setTimeout(() => {
    retryTimer = null;
    if (!stopped) startMfaRealtime();
  }, 5000);
}

export function getUserIdFromToken(token) {
  try {
    const encodedPayload = token?.split('.')[1];
    if (!encodedPayload) return null;
    const base64 = encodedPayload.replace(/-/g, '+').replace(/_/g, '/');
    const padded = base64.padEnd(base64.length + (4 - (base64.length % 4)) % 4, '=');
    const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=';
    let bytes = '';
    for (let index = 0; index < padded.length;) {
      const first = alphabet.indexOf(padded[index++]);
      const second = alphabet.indexOf(padded[index++]);
      const third = alphabet.indexOf(padded[index++]);
      const fourth = alphabet.indexOf(padded[index++]);
      bytes += String.fromCharCode(((first << 2) | (second >> 4)) & 0xff);
      if (third !== 64) bytes += String.fromCharCode(((second << 4) | (third >> 2)) & 0xff);
      if (fourth !== 64) bytes += String.fromCharCode(((third << 6) | fourth) & 0xff);
    }

    const payload = JSON.parse(decodeURIComponent(bytes.split('')
      .map(character => `%${character.charCodeAt(0).toString(16).padStart(2, '0')}`)
      .join('')));
    return payload.sub ?? payload['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier'] ?? null;
  } catch {
    return null;
  }
}

export function isMfaRequestForAccount(payload, userId, deviceId) {
  return Boolean(
    payload?.sessionToken
    && payload.deviceId === deviceId
    && typeof payload.userId === 'string'
    && payload.userId.toLowerCase() === userId?.toLowerCase(),
  );
}
