import {
  ActionIcon,
  Alert,
  Anchor,
  Button,
  Code,
  Group,
  Modal,
  SegmentedControl,
  Stack,
  Text,
  Textarea,
  Tooltip,
} from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { useIsMutating } from '@tanstack/react-query';
import { CircleAlert, CircleCheck, ExternalLink, MessageSquarePlus } from 'lucide-react';
import { useState, type KeyboardEvent } from 'react';
import { useLocation } from 'react-router';
import { useAppConfig } from '../api/context';
import {
  FEEDBACK_SUBMIT_KEY,
  useFeedbackStatus,
  useSubmitFeedback,
  type FeedbackKind,
  type FeedbackResult,
} from '../api/feedback';

/** Until the server says otherwise; it refuses anything longer. */
const DEFAULT_MAX_LENGTH = 4000;

const KINDS = [
  { value: 'bug', label: 'Bug' },
  { value: 'idea', label: 'Idea' },
] satisfies { value: FeedbackKind; label: string }[];

function isKind(value: string): value is FeedbackKind {
  return KINDS.some((kind) => kind.value === value);
}

/** The version without its `+commit` build suffix, as the server writes it into the issue. */
function shortVersion(version: string): string {
  return version.split('+')[0] ?? version;
}

/** What the person is looking at: the page's own heading, else the document title. */
function currentPageTitle(): string | null {
  const heading = document.querySelector('main h1, main h2')?.textContent?.trim();

  if (heading !== undefined && heading !== '') {
    return heading;
  }

  const title = document.title.trim();

  return title === '' ? null : title;
}

/** The header button beside the theme toggle: one text box, sent with the page it was opened on. */
export function FeedbackButton() {
  const [opened, { open, close }] = useDisclosure(false);
  // Closing mid-send would unmount the answer: the issue might be opened with nobody told where.
  const sending = useIsMutating({ mutationKey: FEEDBACK_SUBMIT_KEY }) > 0;

  return (
    <>
      <Tooltip label="Report a bug or suggest an idea">
        <ActionIcon variant="default" size="lg" aria-label="Send feedback" onClick={open}>
          <MessageSquarePlus size={16} />
        </ActionIcon>
      </Tooltip>

      {/* A stray click beside the dialog must not throw away what was typed; Escape and the buttons still close it. */}
      <Modal
        opened={opened}
        onClose={sending ? () => undefined : close}
        closeOnClickOutside={false}
        title="Send feedback"
      >
        {/* Unmounted with the dialog, so each opening starts blank and reads the page it was opened on. */}
        <FeedbackForm onClose={close} />
      </Modal>
    </>
  );
}

function FeedbackForm({ onClose }: { onClose: () => void }) {
  const { version } = useAppConfig();
  const location = useLocation();
  const [page] = useState(() => `${location.pathname}${location.search}`);
  const [pageTitle] = useState(currentPageTitle);
  const [kind, setKind] = useState<FeedbackKind>('bug');
  const [text, setText] = useState('');
  const status = useFeedbackStatus(true);
  const submit = useSubmitFeedback();

  if (submit.data !== undefined) {
    return <FeedbackSent result={submit.data} onClose={onClose} />;
  }

  const trimmed = text.trim();
  const autoRaise = status.data?.autoRaise === true;
  const repository = status.data?.repository ?? 'GitHub';

  const send = () => {
    if (trimmed !== '' && !submit.isPending) {
      submit.mutate({ kind, text: trimmed, page, pageTitle });
    }
  };

  // Ctrl+Enter (Cmd+Enter on a Mac) sends, as in most comment boxes; Enter alone is a new line.
  const sendOnModifiedEnter = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      send();
    }
  };

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        send();
      }}
    >
      <Stack gap="md">
        <SegmentedControl
          aria-label="Kind of feedback"
          data={KINDS}
          value={kind}
          onChange={(value) => {
            if (isKind(value)) {
              setKind(value);
            }
          }}
        />

        <Textarea
          label={kind === 'bug' ? 'What went wrong?' : 'What would you like?'}
          placeholder={
            kind === 'bug'
              ? 'What you did, what you expected, and what happened instead.'
              : 'What would make Wondarr better for you?'
          }
          rows={5}
          resize="vertical"
          maxLength={Number(status.data?.maxTextLength ?? DEFAULT_MAX_LENGTH)}
          value={text}
          onChange={(event) => setText(event.currentTarget.value)}
          onKeyDown={sendOnModifiedEnter}
          data-autofocus
        />

        <Text size="xs" c="dimmed">
          Includes this page, <Code>{page}</Code>
          {pageTitle === null ? '' : ` (${pageTitle})`}, and Wondarr {shortVersion(version)}.{' '}
          {autoRaise
            ? `It becomes a public issue on ${repository}.`
            : `You finish it on ${repository}, where issues are public.`}
        </Text>

        {submit.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {submit.error.message}
          </Alert>
        )}

        <Group justify="flex-end">
          <Button variant="default" onClick={onClose} disabled={submit.isPending}>
            Cancel
          </Button>
          <Button type="submit" loading={submit.isPending} disabled={trimmed === ''}>
            {autoRaise ? 'Send' : 'Continue'}
          </Button>
        </Group>
      </Stack>
    </form>
  );
}

function FeedbackSent({ result, onClose }: { result: FeedbackResult; onClose: () => void }) {
  if (result.raised) {
    return (
      <Stack gap="md">
        <Alert color="green" icon={<CircleCheck size={16} />}>
          Thanks! It is now{' '}
          <Anchor href={result.issueUrl} target="_blank" rel="noopener noreferrer" inherit>
            {result.issueNumber === null ? 'an issue on GitHub' : `issue #${result.issueNumber}`}
          </Anchor>
          .
        </Alert>
        <Group justify="flex-end">
          {/* The form's focused button is gone, so focus moves here instead of falling to the page. */}
          <Button onClick={onClose} autoFocus>
            Close
          </Button>
        </Group>
      </Stack>
    );
  }

  return (
    <Stack gap="md">
      <Text size="sm">
        Saved to Wondarr’s log. <b>Finish on GitHub</b> opens the issue form filled in: press <b>Create</b> there to
        send it.
      </Text>

      {result.reason !== null && (
        <Alert color="yellow" icon={<CircleAlert size={16} />}>
          Wondarr could not open the issue itself. {result.reason}
          {result.mayBeRaised && (
            <>
              {' '}
              GitHub may have opened it anyway: look at the{' '}
              <Anchor href={result.issueListUrl} target="_blank" rel="noopener noreferrer" inherit>
                issue list
              </Anchor>{' '}
              before you send it again.
            </>
          )}
        </Alert>
      )}

      <Group justify="flex-end">
        <Button variant="default" onClick={onClose}>
          Close
        </Button>
        <Button
          component="a"
          href={result.issueUrl}
          target="_blank"
          rel="noopener noreferrer"
          rightSection={<ExternalLink size={14} />}
          onClick={onClose}
          autoFocus
        >
          Finish on GitHub
        </Button>
      </Group>
    </Stack>
  );
}
