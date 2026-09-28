import {
  ActionIcon,
  AppShell,
  Badge,
  Group,
  NavLink as MantineNavLink,
  Text,
  Tooltip,
  useMantineColorScheme,
} from '@mantine/core';
import { Activity, HeartPulse, Library, Moon, Settings, Star, Sun } from 'lucide-react';
import { NavLink, Outlet, useLocation } from 'react-router';
import { useAppConfig } from '../api/context';
import { useHealth } from '../api/hooks';
import { isProblem } from '../api/types';

const NAV_ITEMS = [
  { to: '/library', match: '/library', label: 'Library', icon: Library },
  { to: '/wanted', match: '/wanted', label: 'Wanted', icon: Star },
  { to: '/activity', match: '/activity', label: 'Activity', icon: Activity },
  { to: '/settings', match: '/settings', label: 'Settings', icon: Settings },
  { to: '/system/status', match: '/system', label: 'System', icon: HeartPulse },
] as const;

/** The count of health checks that are not `ok`, or `null` while the list is unknown. */
function useProblemCount(): number | null {
  const health = useHealth();

  return health.data === undefined ? null : health.data.filter(isProblem).length;
}

function HealthBadge({ count }: { count: number }) {
  const label = count === 1 ? '1 health issue' : `${count} health issues`;

  return (
    <Tooltip label={label}>
      <Badge color={count === 0 ? 'green' : 'red'} variant="light" aria-label={label}>
        {count}
      </Badge>
    </Tooltip>
  );
}

function ColorSchemeToggle() {
  const { colorScheme, setColorScheme } = useMantineColorScheme();
  const isDark = colorScheme === 'dark';

  return (
    <Tooltip label={isDark ? 'Switch to the light theme' : 'Switch to the dark theme'}>
      <ActionIcon
        variant="default"
        size="lg"
        aria-label="Toggle the colour scheme"
        onClick={() => setColorScheme(isDark ? 'light' : 'dark')}
      >
        {isDark ? <Sun size={16} /> : <Moon size={16} />}
      </ActionIcon>
    </Tooltip>
  );
}

/** The *arr shell: a fixed navbar, a header with the instance name, health and the theme toggle. */
export function AppLayout() {
  const { instanceName } = useAppConfig();
  const problemCount = useProblemCount();
  const { pathname } = useLocation();

  return (
    <AppShell header={{ height: 56 }} navbar={{ width: 220, breakpoint: 'sm' }} padding="md">
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between">
          <Text fw={600}>{instanceName}</Text>
          <Group gap="sm">
            {problemCount !== null && <HealthBadge count={problemCount} />}
            <ColorSchemeToggle />
          </Group>
        </Group>
      </AppShell.Header>

      <AppShell.Navbar p="xs">
        {NAV_ITEMS.map(({ to, match, label, icon: Icon }) => (
          <MantineNavLink
            key={to}
            component={NavLink}
            to={to}
            active={pathname.startsWith(match)}
            label={label}
            leftSection={<Icon size={16} />}
          />
        ))}
      </AppShell.Navbar>

      <AppShell.Main>
        <Outlet />
      </AppShell.Main>
    </AppShell>
  );
}
