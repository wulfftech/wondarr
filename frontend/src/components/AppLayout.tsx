import {
  ActionIcon,
  AppShell,
  Badge,
  Burger,
  Group,
  NavLink as MantineNavLink,
  Text,
  Tooltip,
  useMantineColorScheme,
} from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import {
  Activity,
  Archive,
  Download,
  GitMerge,
  HeartPulse,
  Library,
  Moon,
  Plus,
  ScrollText,
  Settings,
  Star,
  Sun,
  Timer,
  WifiOff,
} from 'lucide-react';
import { useEffect } from 'react';
import { NavLink, Outlet, useLocation } from 'react-router';
import { useEventStreamConnected } from '../app/signalr';
import { useAppConfig } from '../api/context';
import { useHealth } from '../api/hooks';
import { useMatchQueueTotal } from '../api/references';
import { isProblem } from '../api/types';
import { UpdateBadge } from './UpdateBadge';

const NAV_ITEMS = [
  { to: '/library', match: ['/library', '/song'], label: 'Library', icon: Library },
  { to: '/add', match: ['/add'], label: 'Add songs', icon: Plus },
  { to: '/wanted', match: ['/wanted'], label: 'Wanted', icon: Star },
  { to: '/match', match: ['/match'], label: 'Match', icon: GitMerge },
  { to: '/activity', match: ['/activity'], label: 'Activity', icon: Activity },
  { to: '/settings', match: ['/settings'], label: 'Settings', icon: Settings },
  { to: '/system/status', match: ['/system/status'], label: 'System', icon: HeartPulse },
  { to: '/system/updates', match: ['/system/updates'], label: 'Updates', icon: Download },
  { to: '/system/tasks', match: ['/system/tasks'], label: 'Tasks', icon: Timer },
  { to: '/system/backup', match: ['/system/backup'], label: 'Backup', icon: Archive },
  { to: '/system/logs', match: ['/system/logs'], label: 'Logs', icon: ScrollText },
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
  const matchTotal = useMatchQueueTotal();
  const streamConnected = useEventStreamConnected();
  const { pathname } = useLocation();
  const [navOpened, { toggle: toggleNav, close: closeNav }] = useDisclosure(false);

  // Below the breakpoint the navbar covers the page, so every navigation closes it.
  useEffect(() => {
    closeNav();
  }, [pathname, closeNav]);

  return (
    <AppShell
      header={{ height: 56 }}
      navbar={{ width: 220, breakpoint: 'sm', collapsed: { mobile: !navOpened } }}
      padding="md"
    >
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between" wrap="nowrap">
          <Group gap="sm" wrap="nowrap">
            <Burger opened={navOpened} onClick={toggleNav} hiddenFrom="sm" size="sm" aria-label="Toggle navigation" />
            <Text fw={600}>{instanceName}</Text>
          </Group>
          <Group gap="sm" wrap="nowrap">
            {!streamConnected && (
              <Tooltip label="Live updates disconnected — reconnecting…">
                <Badge
                  color="yellow"
                  variant="light"
                  leftSection={<WifiOff size={12} />}
                  role="status"
                  aria-label="Live updates disconnected — reconnecting…"
                >
                  <Text span visibleFrom="sm" inherit>
                    Live updates disconnected — reconnecting…
                  </Text>
                </Badge>
              </Tooltip>
            )}
            <UpdateBadge />
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
            active={match.some((prefix) => pathname === prefix || pathname.startsWith(`${prefix}/`))}
            label={label}
            leftSection={<Icon size={16} />}
            rightSection={
              label === 'Match' && matchTotal !== null && matchTotal > 0 ? (
                <Badge
                  variant="light"
                  color="yellow"
                  aria-label={matchTotal === 1 ? '1 file to review' : `${matchTotal} files to review`}
                >
                  {matchTotal}
                </Badge>
              ) : undefined
            }
          />
        ))}
      </AppShell.Navbar>

      <AppShell.Main>
        <Outlet />
      </AppShell.Main>
    </AppShell>
  );
}
