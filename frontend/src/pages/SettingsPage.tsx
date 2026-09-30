import { SegmentedControl, Stack, Title } from '@mantine/core';
import { useNavigate, useParams } from 'react-router';
import { LibrarySettingsPage } from './settings/LibrarySettingsPage';
import { PlexSettingsPage } from './settings/PlexSettingsPage';
import { QualityProfilesPage } from './settings/QualityProfilesPage';
import { ReferenceLibrariesPage } from './settings/ReferenceLibrariesPage';
import { SoulseekSettingsPage } from './settings/SoulseekSettingsPage';

/** The Settings sections, and the route segment each one owns. */
type SettingsSection = 'profiles' | 'library' | 'references' | 'soulseek' | 'plex';

const SECTIONS: { value: SettingsSection; label: string }[] = [
  { value: 'profiles', label: 'Quality profiles' },
  { value: 'library', label: 'Library' },
  { value: 'references', label: 'Reference libraries' },
  { value: 'soulseek', label: 'Soulseek' },
  { value: 'plex', label: 'Plex' },
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
      {active === 'plex' && <PlexSettingsPage />}
    </Stack>
  );
}
