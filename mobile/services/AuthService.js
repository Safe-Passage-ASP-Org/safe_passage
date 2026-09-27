import * as SecureStore from 'expo-secure-store';
import { BASE_URL } from '../config';

const TOKEN_KEY = 'auth_token';

/**
 * Logs in with username/password against the backend.
 * Stores the returned JWT in SecureStore on success.
 * Throws an Error with a user-friendly message on failure.
 * @param {string} username
 * @param {string} password
 * @returns {Promise<{token: string, mfaRequired: boolean, sessionToken?: string}>}
 */
export async function login(username, password) {
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

  // Save token to secure storage
  if (data.token) {
    await SecureStore.setItemAsync(TOKEN_KEY, data.token);
  }

  return data;
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
