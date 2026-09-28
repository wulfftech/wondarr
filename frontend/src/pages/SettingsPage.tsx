import { SegmentedControl, Stack, Title } from '@mantine/core';
import { useNavigate, useParams } from 'react-router';
import { LibrarySettingsPage } from './settings/LibrarySettingsPage';
import { QualityProfilesPage } from './settings/QualityProfilesPage';

/** The two Settings sections, and the route segment each one owns. */
type SettingsSection = 'profiles' | 'library';

/** Settings: the quality profiles and the library the songs are filed in. */
export function SettingsPage() {
  const { section } = useParams();
  const navigate = useNavigate();
  const active: SettingsSection = section === 'library' ? 'library' : 'profiles';

  return (
    <Stack gap="lg">
      <Title order={2}>Settings</Title>

      <SegmentedControl
        value={active}
        onChange={(value) => {
          void navigate(`/settings/${value}`);
        }}
        data={[
          { value: 'profiles', label: 'Quality profiles' },
          { value: 'library', label: 'Library' },
        ]}
      />

      {active === 'profiles' ? <QualityProfilesPage /> : <LibrarySettingsPage />}
    </Stack>
  );
}
