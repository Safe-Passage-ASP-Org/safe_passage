jest.mock('expo-device', () => ({
  isRootedExperimentalAsync: jest.fn(),
  osName: 'Android',
  osVersion: '15',
  manufacturer: 'Google',
  modelName: 'Pixel',
}));

jest.mock('expo-application', () => ({
  getAndroidId: jest.fn(() => 'android-123'),
  getIosIdForVendorAsync: jest.fn(async () => 'ios-123'),
  nativeApplicationVersion: '1.0.0',
  nativeBuildVersion: '1',
}));

jest.mock('react-native', () => ({
  Platform: { OS: 'android' },
}));

const Device = require('expo-device');
const { Platform } = require('react-native');
const { collectDevicePosture } = require('../DeviceService');

describe('DeviceService posture collection (SCRUM-54)', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    Platform.OS = 'android';
  });

  it('collects supported native root posture without fabricating screen lock status', async () => {
    Device.isRootedExperimentalAsync.mockResolvedValue(false);

    const posture = await collectDevicePosture();

    expect(posture).toEqual({
      screenLockEnabled: null,
      isRootedOrJailbroken: false,
      signalSource: 'expo-device',
    });
  });

  it('marks unavailable posture explicitly on web instead of simulating checks', async () => {
    Platform.OS = 'web';

    const posture = await collectDevicePosture();

    expect(posture).toEqual({
      screenLockEnabled: null,
      isRootedOrJailbroken: null,
      signalSource: 'unavailable-web',
    });
    expect(Device.isRootedExperimentalAsync).not.toHaveBeenCalled();
  });

  it('falls back to null when native root check fails', async () => {
    Device.isRootedExperimentalAsync.mockRejectedValue(new Error('Unsupported'));

    const posture = await collectDevicePosture();

    expect(posture).toEqual({
      screenLockEnabled: null,
      isRootedOrJailbroken: null,
      signalSource: 'unavailable-native',
    });
  });
});
