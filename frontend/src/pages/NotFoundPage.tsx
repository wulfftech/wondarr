import { Button, Container, Stack, Text, Title } from '@mantine/core';
import { Link } from 'react-router';

/** Shown for any route the router does not know. */
export function NotFoundPage() {
  return (
    <Container size="sm" py="xl">
      <Stack align="center" gap="sm">
        <Title order={2}>Page not found</Title>
        <Text c="dimmed">That page does not exist in Wondarr.</Text>
        <Button component={Link} to="/library" variant="light">
          Back to Library
        </Button>
      </Stack>
    </Container>
  );
}
