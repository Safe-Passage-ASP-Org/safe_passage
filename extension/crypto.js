const KEY_STORAGE = 'sp_vault_key_jwk';

async function getOrCreateVaultKey() {
  const stored = await chrome.storage.local.get(KEY_STORAGE);
  if (stored[KEY_STORAGE]) {
    return crypto.subtle.importKey(
      'jwk',
      stored[KEY_STORAGE],
      { name: 'AES-GCM', length: 256 },
      false,
      ['encrypt', 'decrypt']
    );
  }
  const key = await crypto.subtle.generateKey(
    { name: 'AES-GCM', length: 256 },
    true,
    ['encrypt', 'decrypt']
  );
  const jwk = await crypto.subtle.exportKey('jwk', key);
  await chrome.storage.local.set({ [KEY_STORAGE]: jwk });
  return key;
}

export async function encryptSecret(plaintext) {
  const key = await getOrCreateVaultKey();
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const encoded = new TextEncoder().encode(plaintext);
  const cipher = await crypto.subtle.encrypt({ name: 'AES-GCM', iv }, key, encoded);
  return {
    iv: btoa(String.fromCharCode(...iv)),
    data: btoa(String.fromCharCode(...new Uint8Array(cipher))),
  };
}

export async function decryptSecret(payload) {
  if (!payload?.iv || !payload?.data) return '';
  const key = await getOrCreateVaultKey();
  const iv = Uint8Array.from(atob(payload.iv), (c) => c.charCodeAt(0));
  const data = Uint8Array.from(atob(payload.data), (c) => c.charCodeAt(0));
  const plain = await crypto.subtle.decrypt({ name: 'AES-GCM', iv }, key, data);
  return new TextDecoder().decode(plain);
}
