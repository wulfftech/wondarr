import { SegmentedControl, Stack, Title } from '@mantine/core';
import { useNavigate, useParams } from 'react-router';
import { LibrarySettingsPage } from './settings/LibrarySettingsPage';
import { NotificationsSettingsPage } from './settings/NotificationsSettingsPage';
import { PlexSettingsPage } from './settings/PlexSettingsPage';
import { QualityProfilesPage } from './settings/QualityProfilesPage';
import { ReferenceLibrariesPage } from './settings/ReferenceLibrariesPage';
import { SoulseekSettingsPage } from './settings/SoulseekSettingsPage';
import { YouTubeSettingsPage } from './settings/YouTubeSettingsPage';

/** The Settings sections, and the route segment each one owns. */
type SettingsSection = 'profiles' | 'library' | 'references' | 'soulseek' | 'youtube' | 'plex' | 'notifications';

const SECTIONS: { value: SettingsSection; label: string }[] = [
  { value: 'profiles', label: 'Quality profiles' },
  { value: 'library', label: 'Library' },
  { value: 'references', label: 'Reference libraries' },
  { value: 'soulseek', label: 'Soulseek' },
  { value: 'youtube', label: 'YouTube' },
  { value: 'plex', label: 'Plex' },
  { value: 'notifications', label: 'Notifications' },
];

/** Settings: the quality profiles, the library the songs are filed in, and the bundled slskd. */
export function SettingsPage() {
  const { section } = useParams();
  const navigate = useNavigate();
  const active: SettingsSection = SECTIONS.some((candidate) => candidate.value === section)
    ? (section as SettingsSection)
    : 'profiles';

  return (
    <Stack gap="lg">
      <Title order={2}>Settings</Title>

      <SegmentedControl
        value={active}
        onChange={(value) => {
          void navigate(`/settings/${value}`);
        }}
        data={SECTIONS}
      />

      {active === 'profiles' && <QualityProfilesPage />}
      {active === 'library' && <LibrarySettingsPage />}
      {active === 'references' && <ReferenceLibrariesPage />}
      {active === 'soulseek' && <SoulseekSettingsPage />}
      {active === 'youtube' && <YouTubeSettingsPage />}
      {active === 'plex' && <PlexSettingsPage />}
      {active === 'notifications' && <NotificationsSettingsPage />}
    </Stack>
  );
}
