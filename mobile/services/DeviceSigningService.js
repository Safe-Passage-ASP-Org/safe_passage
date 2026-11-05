import * as Crypto from 'expo-crypto';
import * as SecureStore from 'expo-secure-store';
import { p256 } from '@noble/curves/p256';

const PRIVATE_KEY_STORAGE_KEY = 'mfa_p256_private_key';
const LEGACY_PRIVATE_KEY_STORAGE_KEY = 'mfa_p256_private_key_legacy';
const ACCOUNT_ALIASES_STORAGE_KEY = 'mfa_p256_account_aliases';
const DEVICE_ID_STORAGE_KEY = 'mfa_approval_device_id';

export async function storeApprovalDeviceId(deviceId) {
  if (deviceId) await SecureStore.setItemAsync(DEVICE_ID_STORAGE_KEY, deviceId);
}

export async function getApprovalDeviceId() {
  return SecureStore.getItemAsync(DEVICE_ID_STORAGE_KEY);
}

export async function getDeviceSigningPublicKey(accountIdentifier) {
  const privateKey = await getOrCreatePrivateKey(accountIdentifier
    ? await resolveAccountScope(accountIdentifier)
    : 'legacy');
  return toBase64(p256.getPublicKey(privateKey, false));
}

export async function signMfaApproval(sessionToken, deviceId, accountId) {
  const privateKey = await getOrCreatePrivateKey(accountId ? `account:${accountId}` : 'legacy');
  const message = new TextEncoder().encode(
    `safe-passage:mfa-approval:v1\n${sessionToken}\n${deviceId}`,
  );
  const signature = p256.sign(message, privateKey, { prehash: true }).toCompactRawBytes();
  return toBase64(signature);
}

export async function signDeviceLogin(deviceId, timestamp, accountIdentifier) {
  const scope = accountIdentifier ? await resolveAccountScope(accountIdentifier) : 'legacy';
  const privateKey = await getOrCreatePrivateKey(scope, { allowLegacyFallback: Boolean(accountIdentifier) });
  const message = new TextEncoder().encode(
    `safe-passage:device-login:v1\n${deviceId}\n${timestamp}`,
  );
  const signature = p256.sign(message, privateKey, { prehash: true }).toCompactRawBytes();
  return toBase64(signature);
}

export async function bindSigningKeyToAccount(accountIdentifier, accountId) {
  const normalizedIdentifier = normalizeIdentifier(accountIdentifier);
  if (!normalizedIdentifier || !accountId) return;

  const aliases = await getAccountAliases();
  const aliasScope = `alias:${normalizedIdentifier}`;
  const existingScope = aliases[normalizedIdentifier] ? `account:${aliases[normalizedIdentifier]}` : aliasScope;
  const privateKey = await getOrCreatePrivateKey(existingScope, { allowLegacyFallback: true });
  await SecureStore.setItemAsync(await privateKeyStorageKey(`account:${accountId}`), toHex(privateKey));
  aliases[normalizedIdentifier] = accountId;
  await SecureStore.setItemAsync(ACCOUNT_ALIASES_STORAGE_KEY, JSON.stringify(aliases));
}

async function resolveAccountScope(accountIdentifier) {
  const normalizedIdentifier = normalizeIdentifier(accountIdentifier);
  if (!normalizedIdentifier) return 'legacy';
  if (/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(normalizedIdentifier))
    return `account:${normalizedIdentifier}`;
  const aliases = await getAccountAliases();
  return aliases[normalizedIdentifier]
    ? `account:${aliases[normalizedIdentifier]}`
    : `alias:${normalizedIdentifier}`;
}

async function getAccountAliases() {
  try {
    const stored = await SecureStore.getItemAsync(ACCOUNT_ALIASES_STORAGE_KEY);
    const aliases = stored ? JSON.parse(stored) : {};
    return aliases && typeof aliases === 'object' && !Array.isArray(aliases) ? aliases : {};
  } catch {
    return {};
  }
}

function normalizeIdentifier(identifier) {
  return typeof identifier === 'string' ? identifier.trim().toLowerCase() : '';
}

async function privateKeyStorageKey(scope) {
  const digest = await Crypto.digestStringAsync(Crypto.CryptoDigestAlgorithm.SHA256, scope);
  return `${PRIVATE_KEY_STORAGE_KEY}_${digest}`;
}

async function getOrCreatePrivateKey(scope, { allowLegacyFallback = false } = {}) {
  const storageKey = scope === 'legacy'
    ? LEGACY_PRIVATE_KEY_STORAGE_KEY
    : await privateKeyStorageKey(scope);
  const storedKey = await SecureStore.getItemAsync(storageKey);
  if (storedKey && /^[0-9a-f]{64}$/i.test(storedKey))
    return fromHex(storedKey);

  if (allowLegacyFallback && scope !== 'legacy') {
    const legacyKey = await SecureStore.getItemAsync(PRIVATE_KEY_STORAGE_KEY)
      ?? await SecureStore.getItemAsync(LEGACY_PRIVATE_KEY_STORAGE_KEY);
    if (legacyKey && /^[0-9a-f]{64}$/i.test(legacyKey)) {
      await SecureStore.setItemAsync(LEGACY_PRIVATE_KEY_STORAGE_KEY, legacyKey);
      await SecureStore.deleteItemAsync(PRIVATE_KEY_STORAGE_KEY);
      return fromHex(legacyKey);
    }
  }

  let privateKey;
  let publicKey;
  do {
    privateKey = await Crypto.getRandomBytesAsync(32);
    try {
      publicKey = p256.getPublicKey(privateKey, false);
    } catch {
      publicKey = null;
    }
  } while (!publicKey);

  await SecureStore.setItemAsync(storageKey, toHex(privateKey));
  return privateKey;
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
    return payload.sub
      ?? payload['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier']
      ?? null;
  } catch {
    return null;
  }
}

function toHex(bytes) {
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('');
}

function fromHex(value) {
  return Uint8Array.from(value.match(/.{2}/g), (byte) => parseInt(byte, 16));
}

function toBase64(bytes) {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
  let output = '';
  for (let index = 0; index < bytes.length; index += 3) {
    const first = bytes[index];
    const second = bytes[index + 1];
    const third = bytes[index + 2];
    output += alphabet[first >> 2];
    output += alphabet[((first & 3) << 4) | ((second ?? 0) >> 4)];
    output += second === undefined ? '=' : alphabet[((second & 15) << 2) | ((third ?? 0) >> 6)];
    output += third === undefined ? '=' : alphabet[third & 63];
  }
  return output;
}