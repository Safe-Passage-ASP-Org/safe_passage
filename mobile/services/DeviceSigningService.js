import * as Crypto from 'expo-crypto';
import * as SecureStore from 'expo-secure-store';
import { p256 } from '@noble/curves/p256';

const PRIVATE_KEY_STORAGE_KEY = 'mfa_p256_private_key';
const DEVICE_ID_STORAGE_KEY = 'mfa_approval_device_id';

export async function storeApprovalDeviceId(deviceId) {
  if (deviceId) await SecureStore.setItemAsync(DEVICE_ID_STORAGE_KEY, deviceId);
}

export async function getApprovalDeviceId() {
  return SecureStore.getItemAsync(DEVICE_ID_STORAGE_KEY);
}

export async function getDeviceSigningPublicKey() {
  const privateKey = await getOrCreatePrivateKey();
  return toBase64(p256.getPublicKey(privateKey, false));
}

export async function signMfaApproval(sessionToken, deviceId) {
  const privateKey = await getOrCreatePrivateKey();
  const message = new TextEncoder().encode(
    `safe-passage:mfa-approval:v1\n${sessionToken}\n${deviceId}`,
  );
  const signature = p256.sign(message, privateKey).toCompactRawBytes();
  return toBase64(signature);
}

async function getOrCreatePrivateKey() {
  const storedKey = await SecureStore.getItemAsync(PRIVATE_KEY_STORAGE_KEY);
  if (storedKey && /^[0-9a-f]{64}$/i.test(storedKey))
    return fromHex(storedKey);

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

  await SecureStore.setItemAsync(PRIVATE_KEY_STORAGE_KEY, toHex(privateKey));
  return privateKey;
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