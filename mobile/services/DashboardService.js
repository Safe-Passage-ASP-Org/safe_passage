import { getToken } from './AuthService';
import { BASE_URL, getApplicationHeaders } from '../config';

export function deriveSecurityStatus({ device = null, error = null } = {}) {
  if (error) {
    return {
      state: 'unavailable',
      label: 'Unavailable',
      detail: 'Could not verify device trust with Safe Passage API.',
      icon: 'cloud-offline',
      color: '#f59e0b',
    };
  }

  if (!device) {
    return {
      state: 'unverified',
      label: 'Unverified',
      detail: 'No enrolled device matched this session.',
      icon: 'shield-outline',
      color: '#f59e0b',
    };
  }

  const status = String(device.status ?? '').toUpperCase();
  const trustStatus = String(device.trustStatus ?? '').toUpperCase();

  if (status === 'ACTIVE' && trustStatus === 'TRUSTED') {
    if (device.hasSigningKey === false) {
      return {
        state: 'attention',
        label: 'Key Pending',
        detail: 'Device is trusted, but signing key enrollment is incomplete.',
        icon: 'warning',
        color: '#f59e0b',
      };
    }

    return {
      state: 'protected',
      label: 'Protected',
      detail: `${device.deviceName || 'This device'} is active and trusted.`,
      icon: 'shield-checkmark',
      color: '#10b981',
    };
  }

  if (status === 'PENDING_APPROVAL') {
    return {
      state: 'pending',
      label: 'Pending Approval',
      detail: 'Waiting for administrator approval before full access.',
      icon: 'time',
      color: '#f59e0b',
    };
  }

  if (status === 'DISABLED') {
    return {
      state: 'disabled',
      label: 'Disabled',
      detail: 'This device has been disabled by an administrator.',
      icon: 'ban',
      color: '#ef4444',
    };
  }

  return {
    state: 'untrusted',
    label: 'Untrusted',
    detail: 'This device is not currently trusted.',
    icon: 'warning',
    color: '#ef4444',
  };
}

export async function fetchDashboardData(deviceId) {
  const token = await getToken();
  if (!token || !deviceId) {
    throw new Error('Sign in on an enrolled device.');
  }

  const headers = {
    Authorization: `Bearer ${token}`,
    ...getApplicationHeaders(deviceId),
  };

  const [activityResponse, devicesResponse] = await Promise.all([
    fetch(`${BASE_URL}/api/activity`, { headers }),
    fetch(`${BASE_URL}/api/devices`, { headers }),
  ]);

  if (!activityResponse.ok) {
    throw new Error('Could not load recent alerts.');
  }

  if (!devicesResponse.ok) {
    throw new Error('Could not verify device security status.');
  }

  const [activityData, devicesData] = await Promise.all([
    activityResponse.json(),
    devicesResponse.json(),
  ]);

  const devices = Array.isArray(devicesData) ? devicesData : [];
  const currentDevice = devices.find(
    (candidate) => candidate?.deviceId === deviceId || candidate?.id === deviceId,
  ) ?? null;

  return {
    recentActivity: Array.isArray(activityData) ? activityData.slice(0, 3) : [],
    securityStatus: deriveSecurityStatus({ device: currentDevice }),
  };
}
