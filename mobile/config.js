const defaultApiUrl = 'http://192.168.254.134:5143';
const configuredApiUrl = process.env.EXPO_PUBLIC_API_URL?.trim();

export const BASE_URL = (configuredApiUrl || defaultApiUrl).replace(/\/+$/, '');
export const MOBILE_APPLICATION_ID = '44444444-4444-4444-4444-444444444444';

export function getApplicationHeaders(deviceId) {
	const headers = { 'X-Application-Id': MOBILE_APPLICATION_ID };
	if (deviceId) headers['X-Device-Id'] = deviceId;
	return headers;
}
