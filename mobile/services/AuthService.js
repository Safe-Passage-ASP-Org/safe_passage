import * as SecureStore from 'expo-secure-store';
import { BASE_URL } from '../config';

const TOKEN_KEY = 'auth_token';

/**
 * Logs in with username/password against the backend.
 * Stores the returned access token in SecureStore when MFA is not required.
 * Throws an Error with a user-friendly message on failure.
 * @param {string} username
 * @param {string} password
 * @returns {Promise<{accessToken?: string, mfaRequired: boolean, sessionToken?: string}>}
 */
export async function login(username, password) {
  await SecureStore.deleteItemAsync(TOKEN_KEY);

  let response;
  try {
    response = await fetch(`${BASE_URL}/api/auth/login`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username, password }),
    });
  } catch (networkError) {
    throw new Error('Cannot reach the server. Check your connection.');
  }

  const data = await response.json().catch(() => ({}));

  if (!response.ok) {
    throw new Error(data.detail ?? data.title ?? 'Login failed. Please try again.');
  }

  const accessToken = data.accessToken ?? data.token;
  if (accessToken) await saveAccessToken(accessToken);

  return data;
}

export async function getMfaStatus(sessionToken, signal) {
  let response;
  try {
    response = await fetch(
      `${BASE_URL}/api/auth/mfa/status/${encodeURIComponent(sessionToken)}`,
      { headers: { Accept: 'application/json' }, signal },
    );
  } catch (networkError) {
    if (networkError.name === 'AbortError') throw networkError;
    throw new Error('Cannot reach the server while checking sign-in approval.');
  }

  const data = await response.json().catch(() => ({}));
  if (!response.ok) {
    throw new Error(data.detail ?? data.title ?? 'Could not check sign-in approval.');
  }

  return data;
}

export async function saveAccessToken(accessToken) {
  if (!accessToken) throw new Error('The server did not return an access token.');
  await SecureStore.setItemAsync(TOKEN_KEY, accessToken);
}

/**
 * Retrieves the stored JWT token from SecureStore.
 * Returns null if no token is stored.
 * @returns {Promise<string|null>}
 */
export async function getToken() {
  try {
    return await SecureStore.getItemAsync(TOKEN_KEY);
  } catch {
    return null;
  }
}

/**
 * Clears the stored JWT token from SecureStore (logout).
 */
export async function logout() {
  try {
    await SecureStore.deleteItemAsync(TOKEN_KEY);
  } catch {
    // Best-effort — ignore errors on logout
  }
}
