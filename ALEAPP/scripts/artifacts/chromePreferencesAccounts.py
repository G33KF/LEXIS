__artifacts_v2__ = {
    "get_chromePreferencesAccounts": {
        "name": "Chrome Preferences - Accounts",
        "description": "Entries of the account_info list in the Preferences JSON file of Chromium based browsers",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-08",
        "last_update_date": "2026-10-08",
        "requirements": "none",
        "category": "Chromium",
        "notes": "One row per entry of the top-level account_info list in Preferences, with the "
                 "email, full_name, given_name, gaia, hd, locale and picture_url keys as stored, "
                 "and google.services.last_signed_in_username of the same file on every row. An "
                 "entry records an account the browser kept details for; it does not by itself "
                 "show when, or whether, the account was signed in to the browser. Picture URL is "
                 "a remote address and is not fetched. Validated on a private Android 15 extraction.",
        "paths": ('*/app_chrome/Default/Preferences', '*/app_sbrowser/Default/Preferences',
                  '*/app_opera/Preferences', '*/app_webview/Default/Preferences'),
        "output_types": "standard",
        "artifact_icon": "user",
    }
}

import json
import os

from scripts.ilapfuncs import logfunc, artifact_processor
from scripts.artifacts.chrome import get_browser_name
from scripts.artifacts.storagePathViews import unique_files


@artifact_processor
def get_chromePreferencesAccounts(context):
    data_list = []
    sources = []
    for file_found in map(str, unique_files(context)):
        if os.path.basename(file_found) != 'Preferences':
            continue
        try:
            with open(file_found, encoding='utf-8') as f:
                prefs = json.load(f)
        except (OSError, ValueError) as ex:
            logfunc(f'Unable to read {file_found}: {ex}')
            continue
        accounts = prefs.get('account_info') if isinstance(prefs, dict) else None
        if not isinstance(accounts, list):
            continue
        last = ((prefs.get('google') or {}).get('services') or {}).get('last_signed_in_username', '')
        browser = 'Browser' if 'app_sbrowser' in file_found else get_browser_name(file_found)
        sources.append(file_found)
        for a in filter(lambda x: isinstance(x, dict), accounts):
            data_list.append(tuple(a.get(k, '') for k in ('email', 'full_name', 'given_name', 'gaia', 'hd',
                                                           'locale', 'picture_url'))
                             + (last, browser, file_found))

    data_headers = ('Email', 'Full Name', 'Given Name', 'Gaia ID', 'Hosted Domain (as stored)', 'Locale',
                    'Picture URL', 'Last Signed-In Username (as stored)', 'Browser Name', 'Source File')
    return data_headers, data_list, '\n'.join(sources)
