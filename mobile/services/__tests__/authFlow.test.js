jest.mock('expo-secure-store', () => {
  const values = new Map();
  return {
    __values: values,
    deleteItemAsync: jest.fn(async key => values.delete(key)),
    setItemAsync: jest.fn(async (key, value) => values.set(key, value)),
    getItemAsync: jest.fn(async key => values.get(key) ?? null),
  };
});

jest.mock('expo-crypto', () => ({
  CryptoDigestAlgorithm: { SHA256: 'SHA-256' },
  digestStringAsync: jest.fn(async (_algorithm, value) => {
    const { createHash } = require('node:crypto');
    return createHash('sha256').update(value).digest('hex');
  }),
  getRandomBytesAsync: jest.fn(async () => {
    const { randomBytes } = require('node:crypto');
    return randomBytes(32);
  }),
}));

const SecureStore = require('expo-secure-store');
const { createPublicKey, verify } = require('node:crypto');
const { login, getMfaStatus } = require('../AuthService');
const {
  bindSigningKeyToAccount,
  getDeviceSigningPublicKey,
  getUserIdFromToken,
  signDeviceLogin,
  signMfaApproval,
} = require('../DeviceSigningService');

describe('mobile auth flow', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    SecureStore.__values.clear();
    global.fetch = jest.fn();
  });

  it('logs in and returns an MFA challenge when required', async () => {
    global.fetch.mockResolvedValue({
      ok: true,
      json: async () => ({
        mfaRequired: true,
        sessionToken: 'session-123',
        approvalDeviceId: 'device-123',
      }),
    });

    const result = await login('alice', 'Password123!', 'device-123');

    expect(result.mfaRequired).toBe(true);
    expect(result.sessionToken).toBe('session-123');
    expect(SecureStore.setItemAsync).not.toHaveBeenCalledWith('auth_token', expect.any(String));
    expect(global.fetch).toHaveBeenCalledWith(
      expect.stringContaining('/api/auth/login'),
      expect.objectContaining({ method: 'POST' }),
    );
    const requestBody = JSON.parse(global.fetch.mock.calls[0][1].body);
    expect(requestBody.deviceSignature).toBeTruthy();
    expect(requestBody.deviceSignatureTimestamp).toEqual(expect.any(Number));
  });

  it('signs the MFA approval payload and resolves a token after approval', async () => {
    const signature = await signMfaApproval('session-123', 'device-123');
    expect(typeof signature).toBe('string');
    expect(signature.length).toBeGreaterThan(0);

    const publicKey = Buffer.from(await getDeviceSigningPublicKey(), 'base64');
    const spki = Buffer.concat([
      Buffer.from('3059301306072a8648ce3d020106082a8648ce3d030107034200', 'hex'),
      publicKey,
    ]);
    const key = createPublicKey({ key: spki, format: 'der', type: 'spki' });
    const approvalMessage = Buffer.from(
      'safe-passage:mfa-approval:v1\nsession-123\ndevice-123',
    );
    expect(verify(
      'sha256',
      approvalMessage,
      { key, dsaEncoding: 'ieee-p1363' },
      Buffer.from(signature, 'base64'),
    )).toBe(true);

    const loginMessage = Buffer.from('safe-passage:device-login:v1\ndevice-123\n123456789');
    const loginSignature = await signDeviceLogin('device-123', 123456789);
    expect(verify(
      'sha256',
      loginMessage,
      { key, dsaEncoding: 'ieee-p1363' },
      Buffer.from(loginSignature, 'base64'),
    )).toBe(true);

    global.fetch.mockResolvedValue({
      ok: true,
      json: async () => ({
        status: 'approved',
        accessToken: 'jwt-token',
        tokenType: 'Bearer',
        deviceId: 'device-123',
      }),
    });

    const status = await getMfaStatus('session-123', undefined, 'device-123');
    expect(status.accessToken).toBe('jwt-token');
    expect(status.deviceId).toBe('device-123');
  });

  it('keeps signing keys separate per account and binds enrollment keys to account IDs', async () => {
    const aliceId = 'c0a80123-4567-489a-bcde-123456789abc';
    const aliceKey = await getDeviceSigningPublicKey('alice@example.com');
    const bobKey = await getDeviceSigningPublicKey('bob@example.com');
    expect(aliceKey).not.toBe(bobKey);

    await bindSigningKeyToAccount('alice@example.com', aliceId);
    expect(await getDeviceSigningPublicKey(aliceId)).toBe(aliceKey);
    expect(await getDeviceSigningPublicKey('bob@example.com')).toBe(bobKey);

    const token = `header.${Buffer.from(JSON.stringify({ sub: aliceId })).toString('base64url')}.signature`;
    expect(getUserIdFromToken(token)).toBe(aliceId);
  });
});
