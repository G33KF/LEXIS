__artifacts_v2__ = {
    "get_wellbeing": {
        "name": "Digital Wellbeing - Events",
        "description": "Parses Digital Wellbeing events",
        "author": "@AlexisBrignoni",
        "creation_date": "2020-02-02",
        "last_update_date": "2026-08-01",
        "requirements": "none",
        "category": "Digital Wellbeing",
        "notes": (
            "Event Type shows the UsageEvents.Event constant whose value matches the stored "
            "numeric 'type' field; the names shown are those for values 1, 2, 12, 18, 19, 20, 23, "
            "26 and 27; any other value is shown as stored, whether or not a constant exists for "
            "it. Constant names are reported as defined by Android; a keyguard or screen-state "
            "constant does not by itself establish a user action. Reference: Google, "
            "'UsageEvents.Event', "
            "https://developer.android.com/reference/android/app/usage/UsageEvents.Event, for all "
            "names except NOTIFICATION_INTERRUPTION (12), which is a hidden constant defined in "
            "AOSP UsageEvents.java, "
            "https://android.googlesource.com/platform/frameworks/base/+/refs/tags/android-14.0.0_r1/core/java/android/app/usage/UsageEvents.java#200. "
            "That the Wellbeing events table stores these values is not documented in either "
            "source."
        ),
        "paths": ('*/com.google.android.apps.wellbeing/databases/app_usage*',),
        "output_types": "standard",
        "artifact_icon": "heart",
        "sample_data": {
            "hc_pixel8pro_a16": "Android 16 | com.google.android.apps.wellbeing vc 839927 | 4556 rows",
            "kevin_pocox7_a15": "Android 15 | com.google.android.apps.wellbeing vc 762847 | 18070 rows",
            "pixel7a_a14": "Android 14 | com.google.android.apps.wellbeing vc 550467 | 44715 rows",
            "russell_pixel6a_a13": "Android 13 | com.google.android.apps.wellbeing vc 495937 | 11663 rows",
            "userb2_a13": "Android 13 | com.google.android.apps.wellbeing vc 668567 | 1723 rows",
        },
    },
    "get_wellbeing_url": {
        "name": "Digital Wellbeing - Component Events",
        "description": "Parses Digital Wellbeing events recorded against a package component",
        "author": "@AlexisBrignoni",
        "creation_date": "2020-02-02",
        "last_update_date": "2026-08-10",
        "requirements": "none",
        "category": "Digital Wellbeing",
        "notes": (
            "Component Name is the components.component_name value stored for the event. Event "
            "shows ACTIVITY_RESUMED for a stored type of 1 and ACTIVITY_PAUSED for 2, the "
            "UsageEvents.Event constants with those values; any other value is shown as stored. "
            "None of the listed images produced rows, so this artifact is not exercised by the "
            "listed data. Constant names are reported as defined by Android. Reference: Google, "
            "'UsageEvents.Event', "
            "https://developer.android.com/reference/android/app/usage/UsageEvents.Event. That "
            "the Wellbeing component_events table stores these values is not documented in that "
            "source."
        ),
        "paths": ('*/com.google.android.apps.wellbeing/databases/app_usage*',),
        "output_types": "standard",
        "artifact_icon": "globe",
        "sample_data": {
            "hc_pixel8pro_a16": "Android 16 | com.google.android.apps.wellbeing vc 839927 | 0 rows",
            "kevin_pocox7_a15": "Android 15 | com.google.android.apps.wellbeing vc 762847 | 0 rows",
            "pixel7a_a14": "Android 14 | com.google.android.apps.wellbeing vc 550467 | 0 rows",
            "russell_pixel6a_a13": "Android 13 | com.google.android.apps.wellbeing vc 495937 | 0 rows",
            "userb2_a13": "Android 13 | com.google.android.apps.wellbeing vc 668567 | 0 rows",
        },
    },
    "get_wellbeing_apps": {
        "name": "Digital Wellbeing - App Settings",
        "description": "Apps known to Digital Wellbeing with their app timer and pause settings",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-09",
        "last_update_date": "2026-10-09",
        "requirements": "none",
        "category": "Digital Wellbeing",
        "notes": (
            "Read from the apps and suspensions tables of app_config. One row per package. "
            "System App and Allow Pause are the is_system and allow_suspend columns as stored. "
            "App Timer is limit_millis in minutes and Last Timer Warning is "
            "last_warning_epoch_millis; 0 is the stored default and not by itself evidence that "
            "timers were never used. Paused (as stored) is the reason value of a suspensions row "
            "for the package. The groups, limits, grayscale and session tables are not reported. "
            "A row is not by itself evidence that the app was used. Validated on a private Android 15 extraction."
        ),
        "paths": ('*/com.google.android.apps.wellbeing/databases/app_config*',),
        "output_types": "standard",
        "artifact_icon": "package",
    }
}

import datetime

from scripts.ilapfuncs import artifact_processor, does_table_exist_in_db, open_sqlite_db_readonly


def _ms_to_utc(value):
    if value:
        return datetime.datetime.fromtimestamp(int(value) / 1000, datetime.timezone.utc)
    return ''


def _app_usage_db(files_found):
    for file_found in files_found:
        file_found = str(file_found)
        if file_found.endswith('app_usage'):
            return file_found
    return ''


@artifact_processor
def get_wellbeing(context):
    files_found = context.get_files_found()
    data_list = []
    source_path = _app_usage_db(files_found)
    if source_path:
        db = open_sqlite_db_readonly(source_path)
        cursor = db.cursor()
        cursor.execute('''
            SELECT
            events.timestamp,
            packages.package_name,
            case
                when events.type = 1 THEN 'ACTIVITY_RESUMED'
                when events.type = 2 THEN 'ACTIVITY_PAUSED'
                when events.type = 12 THEN 'NOTIFICATION_INTERRUPTION'
                when events.type = 18 THEN 'KEYGUARD_HIDDEN'
                when events.type = 19 THEN 'FOREGROUND_SERVICE_START'
                when events.type = 20 THEN 'FOREGROUND_SERVICE_STOP'
                when events.type = 23 THEN 'ACTIVITY_STOPPED'
                when events.type = 26 THEN 'DEVICE_SHUTDOWN'
                when events.type = 27 THEN 'DEVICE_STARTUP'
                else events.type
            END as eventtype
            FROM events INNER JOIN packages ON events.package_id=packages._id
        ''')
        all_rows = cursor.fetchall()
        db.close()
        for row in all_rows:
            data_list.append((_ms_to_utc(row[0]), row[1], row[2]))

    data_headers = (('Timestamp', 'datetime'), 'Package Name', 'Event Type')
    return data_headers, data_list, source_path


@artifact_processor
def get_wellbeing_url(context):
    files_found = context.get_files_found()
    data_list = []
    source_path = _app_usage_db(files_found)
    # Older app_usage generations have no component_events table
    # (community report, PR #633).
    if source_path and does_table_exist_in_db(source_path, 'component_events'):
        db = open_sqlite_db_readonly(source_path)
        cursor = db.cursor()
        cursor.execute('''
            SELECT
            component_events.timestamp,
            component_events._id,
            components.package_id,
            packages.package_name,
            components.component_name,
            CASE
                when component_events.type=1 THEN 'ACTIVITY_RESUMED'
                when component_events.type=2 THEN 'ACTIVITY_PAUSED'
                else component_events.type
            END as eventType
            FROM component_events
            INNER JOIN components ON component_events.component_id=components._id
            INNER JOIN packages ON components.package_id=packages._id
            ORDER BY component_events.timestamp
        ''')
        all_rows = cursor.fetchall()
        db.close()
        for row in all_rows:
            data_list.append((_ms_to_utc(row[0]), row[1], row[2], row[3], row[4], row[5]))

    data_headers = (
        ('Timestamp', 'datetime'), 'Event ID', 'Package ID', 'Package Name', 'Component Name', 'Event')
    return data_headers, data_list, source_path


@artifact_processor
def get_wellbeing_apps(context):
    data_list = []
    source_path = ''
    for file_found in map(str, context.get_files_found()):
        if not file_found.endswith('app_config'):
            continue
        source_path = file_found
        db = open_sqlite_db_readonly(file_found)
        if db is None:
            continue
        suspended = (', (SELECT group_concat(reason) FROM suspensions WHERE package_id = apps._id)'
                     if does_table_exist_in_db(file_found, 'suspensions') else ", ''")
        rows = db.execute(f'''
            SELECT package_name, is_system, allow_suspend, limit_millis, last_warning_epoch_millis{suspended}
            FROM apps ORDER BY package_name''').fetchall()
        db.close()
        for package, system, allow, limit, warned, paused in rows:
            data_list.append((package, system, allow, round(limit / 60000, 1) if limit else '',
                              _ms_to_utc(warned), paused or ''))

    data_headers = ('Package Name', 'System App (as stored)', 'Allow Pause (as stored)', 'App Timer (minutes)',
                    ('Last Timer Warning', 'datetime'), 'Paused (as stored)')
    return data_headers, data_list, source_path
