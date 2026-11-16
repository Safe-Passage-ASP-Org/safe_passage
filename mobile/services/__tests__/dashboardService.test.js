jest.mock('../AuthService', () => ({
  getToken: jest.fn(),
}));

const { getToken } = require('../AuthService');
const { deriveSecurityStatus, fetchDashboardData } = require('../DashboardService');

describe('DashboardService (SCRUM-52)', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    global.fetch = jest.fn();
  });

  it('derives Protected status and slices returned activity for loaded state', async () => {
    getToken.mockResolvedValue('jwt-token');
    global.fetch
      .mockResolvedValueOnce({
        ok: true,
        json: async () => ([
          { id: '1', action: 'Login', target: 'Web', time: '1m ago' },
          { id: '2', action: 'MFA Approved', target: 'Mobile', time: '2m ago' },
          { id: '3', action: 'App Launch', target: 'Portal', time: '5m ago' },
          { id: '4', action: 'Older Event', target: 'Portal', time: '1h ago' },
        ]),
      })
      .mockResolvedValueOnce({
        ok: true,
        json: async () => ([
          {
            id: 'guid-1',
            deviceId: 'device-123',
            deviceName: 'Pixel 9',
            status: 'ACTIVE',
            trustStatus: 'TRUSTED',
            hasSigningKey: true,
          },
        ]),
      });

    const result = await fetchDashboardData('device-123');

    expect(result.securityStatus).toEqual({
      state: 'protected',
      label: 'Protected',
      detail: 'Pixel 9 is active and trusted.',
      icon: 'shield-checkmark',
      color: '#10b981',
    });
    expect(result.recentActivity).toHaveLength(3);
    expect(result.recentActivity.map((item) => item.id)).toEqual(['1', '2', '3']);
  });

  it('returns genuine empty activity without fabricated sample rows', async () => {
    getToken.mockResolvedValue('jwt-token');
    global.fetch
      .mockResolvedValueOnce({
        ok: true,
        json: async () => [],
      })
      .mockResolvedValueOnce({
        ok: true,
        json: async () => ([
          {
            id: 'guid-1',
            deviceId: 'device-123',
            deviceName: 'Pixel 9',
            status: 'ACTIVE',
            trustStatus: 'TRUSTED',
            hasSigningKey: true,
          },
        ]),
      });

    const result = await fetchDashboardData('device-123');

    expect(result.recentActivity).toEqual([]);
    expect(result.securityStatus.state).toBe('protected');
  });

  it('derives explicit unverified, pending, key-pending, disabled, and untrusted states', async () => {
    getToken.mockResolvedValue('jwt-token');
    global.fetch
      .mockResolvedValueOnce({ ok: true, json: async () => [] })
      .mockResolvedValueOnce({ ok: true, json: async () => [] });

    const unverified = await fetchDashboardData('missing-device');
    expect(unverified.securityStatus.state).toBe('unverified');
    expect(unverified.securityStatus.label).toBe('Unverified');

    expect(deriveSecurityStatus({
      device: { status: 'PENDING_APPROVAL', trustStatus: 'UNTRUSTED' },
    }).state).toBe('pending');

    expect(deriveSecurityStatus({
      device: { status: 'ACTIVE', trustStatus: 'TRUSTED', hasSigningKey: false },
    }).state).toBe('attention');

    expect(deriveSecurityStatus({
      device: { status: 'DISABLED', trustStatus: 'UNTRUSTED' },
    }).state).toBe('disabled');

    expect(deriveSecurityStatus({
      device: { status: 'ACTIVE', trustStatus: 'UNTRUSTED' },
    }).state).toBe('untrusted');
  });

  it('throws on API error and derives Unavailable security status instead of swallowing failures', async () => {
    getToken.mockResolvedValue('jwt-token');
    global.fetch
      .mockResolvedValueOnce({ ok: false, status: 500 })
      .mockResolvedValueOnce({ ok: true, json: async () => [] });

    await expect(fetchDashboardData('device-123')).rejects.toThrow('Could not load recent alerts.');

    const unavailable = deriveSecurityStatus({ error: new Error('Network error') });
    expect(unavailable).toEqual({
      state: 'unavailable',
      label: 'Unavailable',
      detail: 'Could not verify device trust with Safe Passage API.',
      icon: 'cloud-offline',
      color: '#f59e0b',
    });
  });
});
