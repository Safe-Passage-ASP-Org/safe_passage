import Constants from 'expo-constants';

// Dynamically resolve the host IP from the Metro bundler server
const debuggerHost = Constants.expoConfig?.hostUri || Constants.manifest2?.extra?.expoClient?.hostUri;
const hostIp = debuggerHost ? debuggerHost.split(':')[0] : '192.168.254.103';

const defaultApiUrl = `http://${hostIp}:5143`;
const webDefaultApiUrl = 'http://localhost:5143';
const configuredApiUrl = process.env.EXPO_PUBLIC_API_URL?.trim();
const isWeb = typeof document !== 'undefined';

export const BASE_URL = (configuredApiUrl || (isWeb ? webDefaultApiUrl : defaultApiUrl)).replace(/\/+$/, '');
export const MOBILE_APPLICATION_ID = '44444444-4444-4444-4444-444444444444';

export function getApplicationHeaders(deviceId) {
	const headers = { 'X-Application-Id': MOBILE_APPLICATION_ID };
	if (deviceId) headers['X-Device-Id'] = deviceId;
	return headers;
}
