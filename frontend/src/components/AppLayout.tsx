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
  VisuallyHidden,
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
import { useEffect, useState } from 'react';
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

const DISCONNECTED_MESSAGE = 'Live updates disconnected — reconnecting…';

/** A blip shorter than this is not worth a badge. */
const DISCONNECTED_GRACE_MS = 2000;

/** `active`, but only once it has stayed true for `delayMs`; false again the moment it stops. */
function useAfterGrace(active: boolean, delayMs: number): boolean {
  const [elapsed, setElapsed] = useState(false);

  useEffect(() => {
    if (!active) {
      return undefined;
    }

    const timer = setTimeout(() => setElapsed(true), delayMs);

    return () => {
      clearTimeout(timer);
      setElapsed(false);
    };
  }, [active, delayMs]);

  return active && elapsed;
}

/** The *arr shell: a fixed navbar, a header with the instance name, health and the theme toggle. */
export function AppLayout() {
  const { instanceName } = useAppConfig();
  const problemCount = useProblemCount();
  const matchTotal = useMatchQueueTotal();
  const showDisconnected = useAfterGrace(!useEventStreamConnected(), DISCONNECTED_GRACE_MS);
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
            {showDisconnected && (
              <Tooltip label={DISCONNECTED_MESSAGE}>
                {/* The hidden live region below announces it; the badge itself is only for eyes. */}
                <Badge color="yellow" variant="light" leftSection={<WifiOff size={12} />} aria-hidden>
                  <Text span visibleFrom="sm" inherit>
                    {DISCONNECTED_MESSAGE}
                  </Text>
                </Badge>
              </Tooltip>
            )}
            <VisuallyHidden aria-live="polite" role="status">
              {showDisconnected ? DISCONNECTED_MESSAGE : ''}
            </VisuallyHidden>
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
            // Following the link to the page already open changes no pathname, so close on the tap itself.
            onClick={closeNav}
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
