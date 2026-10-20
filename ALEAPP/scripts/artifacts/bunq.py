__artifacts_v2__ = {
    "bunq_users": {
        "name": "bunq - Account Holders",
        "description": "Account holder profiles cached by the bunq banking app in accessible-users-cache",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-08",
        "last_update_date": "2026-10-08",
        "requirements": "none",
        "category": "bunq",
        "notes": "accessible-users-cache is a JSON list in which each item is a one-key object "
                 "naming the user type (such as UserPerson) around the profile. Every "
                 "column is the field of that name as stored; Aliases joins the alias list as "
                 "type: value. Created and Updated are the stored strings, kept as text because "
                 "their time zone is not established. The file is a cache of what the bunq "
                 "server returned to the app; when it was last refreshed is not recorded in it, "
                 "and a profile in it is not evidence of when the app was last used.",
        "paths": ('*/com.bunq.android/files/*/accessible-users-cache',),
        "output_types": "standard",
        "artifact_icon": "user",
    },
    "bunq_accounts": {
        "name": "bunq - Monetary Accounts",
        "description": "Bank accounts, with IBAN and cached balance, kept by the bunq banking app "
                       "in monetary-account-cache",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-08",
        "last_update_date": "2026-10-08",
        "requirements": "none",
        "category": "bunq",
        "notes": "monetary-account-cache is a JSON list in which each item is a one-key object "
                 "naming the account type (such as MonetaryAccountBank) around the "
                 "account. IBAN is the alias whose type is IBAN; Balance is the cached balance "
                 "value and currency as stored, which is what the server last returned to the app "
                 "and not a balance at any time the file records. Created and Updated are the "
                 "stored strings, kept as text because their time zone is not established. "
                 "Transactions are not in this cache and "
                 "are not reported.",
        "paths": ('*/com.bunq.android/files/*/monetary-account-cache',),
        "output_types": "standard",
        "artifact_icon": "credit-card",
    },
}

import json

from scripts.ilapfuncs import artifact_processor, logfunc


def _items(context, name):
    """Yields (type, object, relative path) for every item of every cache file called name."""
    for file_found in context.get_files_found():
        file_found = str(file_found)
        if not file_found.replace('\\', '/').endswith('/' + name):
            continue
        try:
            with open(file_found, encoding='utf-8') as cache:
                items = json.load(cache)
        except (OSError, ValueError) as error:
            logfunc(f'bunq: could not read {file_found}: {error}')
            continue
        for item in items if isinstance(items, list) else []:
            if isinstance(item, dict) and len(item) == 1:
                kind, obj = next(iter(item.items()))
                if isinstance(obj, dict):
                    yield kind, obj, context.get_relative_path(file_found)


def _alias_list(obj):
    alias = obj.get('alias')
    return alias if isinstance(alias, list) else []


def _aliases(obj):
    return '; '.join(f"{a.get('type', '')}: {a.get('value', '')}"
                     for a in _alias_list(obj) if isinstance(a, dict))


def _address(addr):
    if not isinstance(addr, dict):
        return ''
    parts = (addr.get('street'), addr.get('house_number'), addr.get('postal_code'),
             addr.get('city'), addr.get('country'))
    return ' '.join(str(p) for p in parts if p)


def _money(amount):
    return f"{amount.get('value', '')} {amount.get('currency', '')}".strip() if isinstance(amount, dict) else ''


@artifact_processor
def bunq_users(context):
    data_list, sources = [], []
    for kind, obj, source in _items(context, 'accessible-users-cache'):
        sources.append(source)
        data_list.append((
            obj.get('display_name', ''),
            obj.get('first_name', ''),
            obj.get('last_name', ''),
            obj.get('date_of_birth', ''),
            obj.get('nationality', ''),
            _address(obj.get('address_main')),
            _address(obj.get('address_postal')),
            _aliases(obj),
            obj.get('status', ''),
            obj.get('created', ''),
            obj.get('updated', ''),
            kind,
            obj.get('id', ''),
        ))
    data_headers = ('Display Name', 'First Name', 'Last Name', 'Date of Birth', 'Nationality',
                    'Main Address', 'Postal Address', 'Aliases', 'Status', 'Created (as stored)',
                    'Updated (as stored)', 'User Type', 'User ID')
    return data_headers, data_list, '\n'.join(sorted(set(sources)))


@artifact_processor
def bunq_accounts(context):
    data_list, sources = [], []
    for kind, obj, source in _items(context, 'monetary-account-cache'):
        sources.append(source)
        iban = next((a.get('value', '') for a in _alias_list(obj)
                     if isinstance(a, dict) and a.get('type') == 'IBAN'), '')
        data_list.append((
            obj.get('description', ''),
            iban,
            _money(obj.get('balance')),
            obj.get('currency', ''),
            obj.get('status', ''),
            obj.get('display_name', ''),
            _money(obj.get('daily_limit')),
            _money(obj.get('overdraft_limit')),
            _aliases(obj),
            obj.get('created', ''),
            obj.get('updated', ''),
            kind,
            obj.get('user_id', ''),
            obj.get('id', ''),
        ))
    data_headers = ('Description', 'IBAN', 'Balance (cached)', 'Currency', 'Status', 'Holder Name',
                    'Daily Limit', 'Overdraft Limit', 'Aliases', 'Created (as stored)',
                    'Updated (as stored)', 'Account Type', 'User ID', 'Account ID')
    return data_headers, data_list, '\n'.join(sorted(set(sources)))
