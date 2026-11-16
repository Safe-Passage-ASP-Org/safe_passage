import * as SecureStore from 'expo-secure-store';

const WEB_STORAGE_PREFIX = 'safe-passage.mobile.';
const isWeb = typeof window !== 'undefined' && typeof window.localStorage !== 'undefined';

// The browser fallback supports local development demos only. Native builds
// continue to keep access tokens and private signing keys in OS secure storage.
export async function getItemAsync(key) {
  if (!isWeb) return SecureStore.getItemAsync(key);
  return globalThis.localStorage.getItem(`${WEB_STORAGE_PREFIX}${key}`);
}

export async function setItemAsync(key, value) {
  if (!isWeb) return SecureStore.setItemAsync(key, value);
  globalThis.localStorage.setItem(`${WEB_STORAGE_PREFIX}${key}`, value);
}

export async function deleteItemAsync(key) {
  if (!isWeb) return SecureStore.deleteItemAsync(key);
  globalThis.localStorage.removeItem(`${WEB_STORAGE_PREFIX}${key}`);
}
