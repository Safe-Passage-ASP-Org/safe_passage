import { DatabaseSync } from 'node:sqlite';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const [deviceId, signingPublicKey] = process.argv.slice(2);
if (!deviceId || !signingPublicKey) {
  console.error('Usage: node backend/scripts/enroll-device-signing-key.mjs <deviceId> <base64-public-key>');
  process.exit(2);
}

let publicKey;
try {
  publicKey = Buffer.from(signingPublicKey, 'base64');
} catch {
  console.error('The signing public key must be valid base64.');
  process.exit(2);
}

if (publicKey.length !== 65 || publicKey[0] !== 4 || publicKey.toString('base64') !== signingPublicKey) {
  console.error('Expected an uncompressed P-256 public key encoded as base64.');
  process.exit(2);
}

const backendDirectory = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const databasePath = resolve(backendDirectory, 'Data', 'safepassage.db');
const database = new DatabaseSync(databasePath);

try {
  const result = database.prepare(`
    UPDATE Devices
    SET SigningPublicKey = ?
    WHERE DeviceId = ?
      AND Status = 'Active'
      AND TrustStatus = 'Trusted'
      AND (SigningPublicKey IS NULL OR SigningPublicKey = '')
  `).run(signingPublicKey, deviceId);

  if (result.changes !== 1) {
    const existing = database.prepare('SELECT Status, TrustStatus, SigningPublicKey FROM Devices WHERE DeviceId = ?').get(deviceId);
    if (existing?.SigningPublicKey === signingPublicKey) {
      console.log('This public key is already enrolled for the trusted device.');
    } else {
      console.error('No unenrolled active trusted device matched that device ID; no data was changed.');
      process.exitCode = 1;
    }
  } else {
    console.log('Public signing key enrolled. The private key remains on the phone.');
  }
} finally {
  database.close();
}