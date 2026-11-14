import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { BASE_URL } from '../config';
import { getToken } from './AuthService';
import { getApprovalDeviceId, getUserIdFromToken } from './DeviceSigningService';

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

export function isMfaRequestForAccount(payload, userId, deviceId) {
  return Boolean(
    payload?.sessionToken
    && payload.deviceId === deviceId
    && typeof payload.userId === 'string'
    && payload.userId.toLowerCase() === userId?.toLowerCase(),
  );
}
