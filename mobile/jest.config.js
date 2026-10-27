module.exports = {
  testEnvironment: 'node',
  transform: {
    '^.+\\.js$': 'babel-jest',
  },
  moduleNameMapper: {
    '^expo/virtual/env$': '<rootDir>/services/__tests__/expo-env.js',
  },
  moduleFileExtensions: ['js', 'json'],
  testMatch: ['**/services/**/*.test.js'],
};
