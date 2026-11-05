jest.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: jest.fn(),
  LogLevel: { Warning: 3 },
}));
jest.mock('../AuthService', () => ({ getToken: jest.fn() }));
jest.mock('../DeviceSigningService', () => ({ getApprovalDeviceId: jest.fn() }));

const { getUserIdFromToken, isMfaRequestForAccount } = require('../RealtimeService');

function createToken(payload) {
  const encoded = Buffer.from(JSON.stringify(payload)).toString('base64url');
  return `header.${encoded}.signature`;
}

describe('MFA realtime account isolation', () => {
  it('reads the account ID from the access token', () => {
    expect(getUserIdFromToken(createToken({ sub: 'account-123' }))).toBe('account-123');
  });

  it('ignores a valid request addressed to a different account on the same phone', () => {
    const request = {
      sessionToken: 'session-123',
      deviceId: 'shared-phone',
      userId: 'another-account',
    };

    expect(isMfaRequestForAccount(request, 'signed-in-account', 'shared-phone')).toBe(false);
    expect(isMfaRequestForAccount(request, 'another-account', 'shared-phone')).toBe(true);
  });

  it('rejects requests without account or device identity', () => {
    expect(isMfaRequestForAccount({ sessionToken: 'session-123' }, 'account-123', 'phone-1')).toBe(false);
    expect(isMfaRequestForAccount({
      sessionToken: 'session-123',
      deviceId: 'phone-1',
    }, 'account-123', 'phone-1')).toBe(false);
  });
});
