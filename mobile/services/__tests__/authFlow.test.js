jest.mock('expo-secure-store', () => ({
  deleteItemAsync: jest.fn(async () => {}),
  setItemAsync: jest.fn(async () => {}),
  getItemAsync: jest.fn(async () => null),
}));

jest.mock('expo-crypto', () => ({
  getRandomBytesAsync: jest.fn(async () => Uint8Array.from(Array.from({ length: 32 }, (_, index) => index + 1))),
}));

const SecureStore = require('expo-secure-store');
const { login, getMfaStatus } = require('../AuthService');
const { signMfaApproval } = require('../DeviceSigningService');

describe('mobile auth flow', () => {
  beforeEach(() => {
    jest.clearAllMocks();
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
  });

  it('signs the MFA approval payload and resolves a token after approval', async () => {
    const signature = await signMfaApproval('session-123', 'device-123');
    expect(typeof signature).toBe('string');
    expect(signature.length).toBeGreaterThan(0);

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
});
