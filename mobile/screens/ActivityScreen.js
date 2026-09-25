import React from 'react';
import { View, Text, StyleSheet, FlatList } from 'react-native';
import { Ionicons } from '@expo/vector-icons';

const mockActivity = [
  { id: '1', action: 'Successful Login', target: 'Web Portal', time: '10 mins ago', icon: 'log-in', color: '#10b981' },
  { id: '2', action: 'App Access Granted', target: 'Salesforce', time: '1 hour ago', icon: 'apps', color: '#0ea5e9' },
  { id: '3', action: 'MFA Approved', target: 'VPN Gateway', time: '3 hours ago', icon: 'shield-checkmark', color: '#10b981' },
  { id: '4', action: 'Login Denied', target: 'AWS Console', time: 'Yesterday', icon: 'close-circle', color: '#ef4444' },
  { id: '5', action: 'Device Registered', target: 'iPhone 13', time: '2 days ago', icon: 'hardware-chip', color: '#f59e0b' },
];

export default function ActivityScreen() {
  const renderItem = ({ item }) => (
    <View style={styles.activityItem}>
      <View style={[styles.iconContainer, { backgroundColor: `${item.color}20` }]}>
        <Ionicons name={item.icon} size={20} color={item.color} />
      </View>
      <View style={styles.activityInfo}>
        <Text style={styles.activityAction}>{item.action}</Text>
        <Text style={styles.activityTarget}>{item.target}</Text>
      </View>
      <Text style={styles.activityTime}>{item.time}</Text>
    </View>
  );

  return (
    <View style={styles.container}>
      <View style={styles.timelineContainer}>
        <FlatList
          data={mockActivity}
          keyExtractor={item => item.id}
          renderItem={renderItem}
          contentContainerStyle={styles.listContent}
        />
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: '#020617',
  },
  timelineContainer: {
    flex: 1,
    backgroundColor: '#0f172a',
    margin: 16,
    borderRadius: 16,
    borderWidth: 1,
    borderColor: '#1e293b',
    overflow: 'hidden',
  },
  listContent: {
    padding: 16,
  },
  activityItem: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingVertical: 16,
    borderBottomWidth: 1,
    borderBottomColor: '#1e293b',
  },
  iconContainer: {
    padding: 10,
    borderRadius: 10,
    marginRight: 16,
  },
  activityInfo: {
    flex: 1,
  },
  activityAction: {
    fontSize: 16,
    fontWeight: '500',
    color: '#f8fafc',
    marginBottom: 4,
  },
  activityTarget: {
    fontSize: 14,
    color: '#94a3b8',
  },
  activityTime: {
    fontSize: 12,
    color: '#64748b',
  },
});
