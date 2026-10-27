"""LEXIS: Signal Android edits, deletions, mentions and stories. Signal - Messages (same key and name)
gets Edit, Deleted By, Mentions, Mentions Me and Story; Signal - Message Edits lists every revision of
an edited message and Signal - Stories the stories. Built on a private copy of signalAndroid.py, so
the keys, the decryption and the stores are the standard ones."""
import copy
import importlib.util
import pathlib

from scripts.ilapfuncs import artifact_processor, convert_unix_ts_to_utc, logfunc


def _load(name):
    spec = importlib.util.spec_from_file_location(
        f'lexis_{name}_base', pathlib.Path(__file__).resolve().parents[1] / 'artifacts' / f'{name}.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


sig = _load('signalAndroid')

EXTRA_HEADERS = ('Edit', 'Deleted By', 'Mentions', 'Mentions Me', 'Story')
_SOURCE = ('Columns and tables from Signal-Android MessageTable.kt and MentionTable.kt at commit '
           'ad141bb6af88ef67aea05d3d0e55cf9760fe56eb (format only): an edit inserts a new message row and the '
           'earlier revisions point to it through latest_revision_id, original_message_id names the first one '
           'and revision_number the order; deleted_by is the recipient that deleted the message for everyone '
           '(MessageTable.kt marks remote_deleted as deprecated in its favour); mentions_self flags a message '
           'mentioning the local user; story_type above 0 is a story and parent_story_id the story a reply '
           'answers (negative for a group reply). Verified on synthetic databases only. A column or table the '
           'database lacks leaves its column blank.')


def _names(connection):
    """{recipient _id: display name}, as signalAndroid shows senders."""
    columns = sig._table_columns(connection, 'recipient')  # pylint: disable=protected-access
    if '_id' not in columns:
        return {}
    picks = ', '.join(sig._recipient_column(columns, 'r', c)  # pylint: disable=protected-access
                      for c in ('profile_joined_name', 'system_joined_name', 'e164', 'username'))
    return {row[0]: sig._display_name(*row[1:]) or f'Recipient {row[0]}'  # pylint: disable=protected-access
            for row in connection.execute(f'SELECT r._id, {picks} FROM recipient r')}


def _extras(connection):
    """{message _id: (Edit, Deleted By, Mentions, Mentions Me, Story)} for the rows with any of them."""
    columns = sig._table_columns(connection, 'message')  # pylint: disable=protected-access
    wanted = ('latest_revision_id', 'original_message_id', 'revision_number', 'deleted_by', 'mentions_self',
              'story_type', 'parent_story_id')
    names, out = _names(connection), {}
    mentions = {}
    if {'message_id', 'recipient_id'} <= sig._table_columns(connection, 'mention'):  # pylint: disable=protected-access
        for message_id, recipient_id in connection.execute(
                'SELECT message_id, recipient_id FROM mention ORDER BY message_id, range_start, _id'):
            mentions.setdefault(message_id, []).append(names.get(recipient_id, f'Recipient {recipient_id}'))
    if not set(wanted) & columns and not mentions:
        return out
    select = sig._select_list(columns, 'message', wanted)  # pylint: disable=protected-access
    for row_id, latest, original, revision, deleted_by, mentions_self, story_type, parent in connection.execute(
            f'SELECT message._id, {select} FROM message'):
        edit = (f'Replaced by edit (message {latest})' if latest
                else f'Edited (revision {revision}, original message {original})' if original and original != row_id
                else '')
        story = ('Story' if (story_type or 0) > 0
                 else f'Reply to story (message {abs(parent)})' if parent else '')
        values = (edit, names.get(deleted_by, f'Recipient {deleted_by}') if deleted_by else '',
                  ', '.join(mentions.get(row_id, [])), 'Yes' if (mentions_self or 0) > 0 else '', story)
        if any(values):
            out[row_id] = values
    return out


_BaseStores = sig._SignalStores  # pylint: disable=protected-access


class _MessageStores(_BaseStores):
    """The standard stores; each database's message rows get the extra columns, matched on the Message ID
    the standard artifact puts last."""

    def __iter__(self):
        for item in super().__iter__():
            try:
                extras = _extras(item[0])
            except Exception as ex:  # pylint: disable=broad-exception-caught
                logfunc(f'LEXIS Signal: edit, deletion, mention and story columns not read: {ex}')
                extras = {}
            start = len(self.data_list)
            yield item
            self.data_list[start:] = [tuple(row) + extras.get(row[-1], ('',) * len(EXTRA_HEADERS))
                                      for row in self.data_list[start:]]

    def result(self, data_headers):
        return super().result(tuple(data_headers) + EXTRA_HEADERS)


sig._SignalStores = _MessageStores  # pylint: disable=protected-access


def _direction(kind):
    return sig.MESSAGE_DIRECTIONS.get(kind & sig.MESSAGE_BASE_TYPE_MASK, 'Unknown') if kind is not None else 'Unknown'


@artifact_processor
def get_signalMessageEdits(context):
    data_list = []
    stores = _BaseStores(context, data_list)
    for connection, _, _, _ in stores:
        columns = sig._table_columns(connection, 'message')  # pylint: disable=protected-access
        if {'original_message_id', 'latest_revision_id'} <= columns:
            names = _names(connection)
            select = sig._select_list(columns, 'message', ['date_sent', 'original_message_id', 'revision_number',  # pylint: disable=protected-access
                                                           'latest_revision_id', 'from_recipient_id', 'body',
                                                           'thread_id', '_id'])
            for sent, original, revision, latest, sender, body, thread, row_id in connection.execute(
                    f'SELECT {select} FROM message WHERE original_message_id IS NOT NULL OR _id IN '
                    '(SELECT original_message_id FROM message) ORDER BY COALESCE(original_message_id, _id), '
                    'revision_number, _id'):
                data_list.append((convert_unix_ts_to_utc(sent), original or row_id, revision,
                                  'No' if latest else 'Yes', names.get(sender, ''), body or '', thread, row_id))
        else:
            logfunc('LEXIS Signal: this Signal version records no message edits')
        connection.close()
    return stores.result((('Date Sent', 'datetime'), 'Original Message ID', 'Revision', 'Current', 'Sender',
                          'Message', 'Thread ID', 'Message ID'))


@artifact_processor
def get_signalStories(context):
    data_list = []
    stores = _BaseStores(context, data_list)
    for connection, _, _, _ in stores:
        columns = sig._table_columns(connection, 'message')  # pylint: disable=protected-access
        if 'story_type' in columns:
            names = _names(connection)
            replies = {}
            if 'parent_story_id' in columns:
                for parent, count in connection.execute(
                        'SELECT ABS(parent_story_id), COUNT(*) FROM message WHERE parent_story_id != 0 GROUP BY 1'):
                    replies[parent] = count
            select = sig._select_list(columns, 'message', ['date_sent', 'from_recipient_id', 'type', 'body',  # pylint: disable=protected-access
                                                           'story_type', 'story_archived', 'thread_id', '_id'])
            for sent, sender, kind, body, story_type, archived, thread, row_id in connection.execute(
                    f'SELECT {select} FROM message WHERE story_type > 0 ORDER BY date_sent'):
                data_list.append((convert_unix_ts_to_utc(sent), names.get(sender, ''), _direction(kind), body or '',
                                  story_type, 'Yes' if archived else 'No', replies.get(row_id, 0), thread, row_id))
        else:
            logfunc('LEXIS Signal: this Signal version records no stories')
        connection.close()
    return stores.result((('Date Sent', 'datetime'), 'Sender', 'Direction', 'Message', 'Story Type (as stored)',
                          'Archived', 'Replies', 'Thread ID', 'Message ID'))


_base = sig.__artifacts_v2__['get_signalMessages']
__artifacts_v2__ = {'get_signalMessages': dict(
    copy.deepcopy(_base),
    author=f"{_base['author']}, @gk-forensics", last_update_date='2026-10-22',
    notes=_base['notes'] + ' LEXIS: this module replaces the artifact with the same key and name and adds '
                           'Edit, Deleted By, Mentions, Mentions Me and Story, matched on Message ID. ' + _SOURCE)}
_NEW = {'author': '@gk-forensics', 'creation_date': '2026-10-22', 'last_update_date': '2026-10-22',
        'requirements': 'none', 'category': 'Signal', 'output_types': 'standard', 'paths': _base['paths']}
__artifacts_v2__['get_signalMessageEdits'] = dict(
    _NEW, name='Signal - Message Edits', artifact_icon='edit',
    description='Every revision of each edited Signal message, in order, with the current one marked',
    notes='Requires the SQLCipher key from extra/Secrets/secrets.json, as Signal - Messages. ' + _SOURCE)
__artifacts_v2__['get_signalStories'] = dict(
    _NEW, name='Signal - Stories', artifact_icon='image',
    description='Signal stories sent and received: time, author, text, archived, and how many replies each has',
    notes='Requires the SQLCipher key from extra/Secrets/secrets.json, as Signal - Messages. Replies counts the '
          'messages whose parent_story_id names the story. ' + _SOURCE)


def _replacement(func):
    def run(context):
        return func.__wrapped__(context)
    run.__name__ = run.__qualname__ = func.__name__   # the name decides the artifact and its LAVA table
    return artifact_processor(run)


get_signalMessages = _replacement(sig.get_signalMessages)
