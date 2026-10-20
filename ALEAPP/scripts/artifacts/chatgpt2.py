__artifacts_v2__ = {
    "get_chatpgt2": {
        "name": "ChatGPT - Conversations",
        "description": "Messages of the ChatGPT conversations databases, one row per message with its conversation, role and attachments",
        "author": "Alexis Brignoni, Claude",
        "creation_date": "2025-07-08",
        "last_update_date": "2026-10-19",
        "requirements": "none",
        "category": "ChatGPT",
        "notes": (
            "Every <account>_conversations.db found is read, one per Android user and account, and "
            "Account is the part of the file name before _conversations.db. Where an extraction holds "
            "the same database under more than one storage path, one copy is read. Messages are "
            "rebuilt from DBMessageChunk, the chunks of each message joined in chunkIndex order, and "
            "from DBMessage.messageNode where that column exists for a message no chunk holds. A "
            "message whose JSON does not parse is kept with its text as stored and blank fields. A "
            "conversation of DBConversation with no message stored is listed once with its title and "
            "times only, so every conversation appears. Role is content.role, or content.author.role, "
            "as stored. Attachments shows the files named by the message's attachments (their id or "
            "name) found by exact file name, with or without extension, in the app's cache/files or "
            "files folders of the same Android user; Attachment Names lists what the message records. "
            "The attachment fields and their link to files on disk follow the shape of the app's "
            "message JSON as read from the code and have not been checked against an extraction "
            "holding attachments."
        ),
        "paths": ('**/com.openai.chatgpt/databases/*conversations.db*',
                  '**/com.openai.chatgpt/cache/files/*',
                  '**/com.openai.chatgpt/files/*'),
        "output_types": "standard",
        "artifact_icon": "message-circle",
        "sample_data": {
            "anne_a15": "Android 15 | com.openai.chatgpt vc 2525902 | 12 rows",
        },
        "data_views": {
            "conversation": {
                "conversationDiscriminatorColumn": "Conversation ID",
                "conversationLabelColumn": "Conversation Title",
                "textColumn": "Content",
                "directionColumn": "Role",
                "directionSentValue": "user",
                "timeColumn": "Creation Time",
                "senderColumn": "Role",
                "mediaColumn": "Attachments",
            }
        },
    }
}

import datetime
import json
import os
from collections import defaultdict

from scripts.artifacts.storagePathViews import canonical_path, unique_files
from scripts.ilapfuncs import (artifact_processor, check_in_media, does_column_exist_in_db,
                               does_table_exist_in_db, logfunc, open_sqlite_db_readonly)

_PACKAGE = 'com.openai.chatgpt'


def _container(context, path):
    '''The app's data folder the file sits in, the same for every storage view of it.'''
    key = canonical_path(context.get_relative_path(path))[0].replace('\\', '/')
    parts = key.split('/')
    return '/'.join(parts[:parts.index(_PACKAGE) + 1]) if _PACKAGE in parts else key


def _utc(value):
    if not value or not isinstance(value, str):
        return ''
    try:
        return datetime.datetime.fromisoformat(value.replace('Z', '+00:00')).astimezone(datetime.timezone.utc)
    except ValueError:
        return ''


def _text(value):
    '''The readable text of a content value: a string, the parts of a list, or a dict's text.'''
    if value is None:
        return ''
    if isinstance(value, str):
        return value
    if isinstance(value, list):
        return '\n'.join(t for t in (_text(v) for v in value) if t)
    if isinstance(value, dict):
        for key in ('text', 'content', 'parts', 'result'):
            if key in value:
                return _text(value[key])
    return json.dumps(value, ensure_ascii=False)


def _attachments(content):
    '''The attachments a message records, each as the names it may be stored under.'''
    found = []
    inner = content.get('content') if isinstance(content.get('content'), dict) else {}
    for source in (content.get('attachments'), inner.get('attachments'),
                   (content.get('metadata') or {}).get('attachments') if isinstance(content.get('metadata'), dict) else None):
        if isinstance(source, list):
            for att in source:
                if isinstance(att, dict):
                    keys = [str(att[k]) for k in ('id', 'file_id', 'name', 'file_name', 'fileName') if att.get(k)]
                    if keys:
                        found.append(keys)
                elif isinstance(att, str) and att:
                    found.append([att])
    # pictures in a multimodal message point at their file as file-service://<id>
    for part in inner.get('parts') or (content.get('content') if isinstance(content.get('content'), list) else []) or []:
        if isinstance(part, dict) and isinstance(part.get('asset_pointer'), str):
            found.append([part['asset_pointer'].rsplit('/', 1)[-1]])
    return found


def _messages(db, path):
    '''(message id, parsed JSON or the raw text) from DBMessageChunk, then DBMessage.messageNode.'''
    chunks = defaultdict(list)
    if does_table_exist_in_db(path, 'DBMessageChunk'):
        for message_id, index, chunk in db.execute('SELECT messageId, chunkIndex, chunk FROM DBMessageChunk'):
            text = chunk.decode('utf-8', errors='replace') if isinstance(chunk, bytes) else str(chunk or '')
            chunks[message_id].append((index or 0, text))
    messages = {}
    for message_id, parts in chunks.items():
        raw = ''.join(t for _, t in sorted(parts, key=lambda p: p[0]))
        try:
            messages[message_id] = json.loads(raw)
        except json.JSONDecodeError:
            logfunc(f'ChatGPT: message {message_id} does not parse as JSON; its text is kept as stored')
            messages[message_id] = raw
    if does_column_exist_in_db(path, 'DBMessage', 'messageNode'):
        for message_id, node in db.execute('SELECT id, messageNode FROM DBMessage'):
            if message_id in messages or node is None:
                continue
            try:
                messages[message_id] = json.loads(node)
            except (json.JSONDecodeError, TypeError):
                messages[message_id] = str(node)
    return messages


@artifact_processor
def get_chatpgt2(context):
    files = unique_files(context)
    # files on disk by (app container, name) and (app container, name without extension)
    on_disk = {}
    for f in files:
        if os.path.isfile(f) and '/databases/' not in f.replace('\\', '/'):
            name = os.path.basename(f)
            container = _container(context, f)
            on_disk.setdefault((container, name), f)
            on_disk.setdefault((container, os.path.splitext(name)[0]), f)

    data_list = []
    sources = []
    for path in files:
        if not path.endswith('conversations.db'):
            continue
        db = open_sqlite_db_readonly(path)
        if db is None:
            continue
        source = context.get_relative_path(path)
        sources.append(source)
        account = os.path.basename(path)[:-len('_conversations.db')] if path.endswith('_conversations.db') else ''
        container = _container(context, path)
        conversations = {}
        if does_table_exist_in_db(path, 'DBConversation'):
            for conv_id, conv in db.execute('SELECT id, conversation FROM DBConversation'):
                try:
                    conversations[conv_id] = json.loads(conv) if conv else {}
                except (json.JSONDecodeError, TypeError):
                    conversations[conv_id] = {}
        seen = set()
        for message_id, message in _messages(db, path).items():
            content = message.get('content') if isinstance(message, dict) else None
            if not isinstance(content, dict):
                data_list.append(('', '', account, '', '', _text(message), '', '', '', message_id, '', source))
                continue
            conv_id = content.get('conversation_id', '')
            seen.add(conv_id)
            conv = conversations.get(conv_id) or {}
            body = content.get('content')
            references = body.get('references') if isinstance(body, dict) else ''
            author = content.get('author') if isinstance(content.get('author'), dict) else {}
            role = content.get('role') or author.get('role') or ''
            names, media = [], []
            for keys in _attachments(content):
                names.append(keys[-1])
                ref = next((on_disk[(container, k)] for k in keys if (container, k) in on_disk), None)
                if ref:
                    media_ref = check_in_media(ref, os.path.basename(ref))
                    if media_ref:
                        media.append(media_ref)
            data_list.append((_utc(content.get('created_date') or content.get('date')),
                              _utc(content.get('modification_date')), account, conv.get('title', ''), role,
                              _text(body), media[0] if len(media) == 1 else media or '', '\n'.join(names),
                              json.dumps(references, ensure_ascii=False) if references else '',
                              message_id, conv_id, source))
        for conv_id, conv in conversations.items():
            if conv_id not in seen:
                data_list.append((_utc(conv.get('creation_date')), _utc(conv.get('modification_date')), account,
                                  conv.get('title', ''), '', '', '', '', '', '', conv_id, source))
        db.close()

    data_headers = (('Creation Time', 'datetime'), ('Modified Time', 'datetime'), 'Account', 'Conversation Title',
                    'Role', 'Content', ('Attachments', 'media'), 'Attachment Names', 'Content References',
                    'Message ID', 'Conversation ID', 'Source File')
    if len(sources) <= 1:
        data_headers = data_headers[:-1]
        data_list = [row[:-1] for row in data_list]
    return data_headers, data_list, '\n'.join(sources)
