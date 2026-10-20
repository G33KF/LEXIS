using Microsoft.Data.Sqlite;

namespace AleappReader.Core.Tests;

/// <summary>A small case laid out exactly like ALEAPP's LAVA output, built in a temp folder.</summary>
public sealed class SyntheticCase : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "AleappReaderTests", Guid.NewGuid().ToString("N"));
    public string Report => Path.Combine(Root, "ALEAPP_Reports_test");
    public const long T0 = 1714557600; // 2024-05-01 10:00:00 UTC

    public SyntheticCase()
    {
        Directory.CreateDirectory(Path.Combine(Report, "media"));
        Directory.CreateDirectory(Path.Combine(Report, "_Timeline"));
        File.WriteAllBytes(Path.Combine(Report, "media", "aaa.jpg"), [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
        File.WriteAllBytes(Path.Combine(Report, "media", "bbb.png"), [0x89, 0x50, 0x4E, 0x47]);
        Directory.CreateDirectory(Path.Combine(Report, "data", "Dump", "data", "data", "com.example.chat", "databases"));
        File.WriteAllText(Path.Combine(Report, "data", "Dump", "data", "data", "com.example.chat", "databases", "chat.db"), "source db copy");

        File.WriteAllText(Path.Combine(Report, "_lava_data.lava"), """
        {
          "lava_schema_version": 2,
          "parser_info": {"leapp_name": "ALEAPP", "leapp_version": "test"},
          "param_input": "C:/in/extraction.zip", "param_type": "zip", "param_profile": null,
          "processing_status": "Complete",
          "lava_db_name": "_lava_artifacts.db",
          "modules": [{"module_name": "chat", "module_status": "Complete"}],
          "artifacts": {
            "Messages": [{
              "artifact_key": "get_chat", "name": "Chat Messages", "tablename": "get_chat", "module": "chat",
              "column_map": {"timestamp": "Timestamp", "thread_id": "Thread ID", "body": "Body",
                             "direction": "Direction", "from": "From", "media": "Media", "msg_id": "Message ID", "account_id": "Account ID"},
              "record_count": 4, "source_path": "Dump\\data\\data\\com.example.chat\\databases\\chat.db",
              "object_columns": [{"name": "timestamp", "type": "datetime"}, {"name": "media", "type": "media"}],
              "data_views": {"conversation": {"conversationDiscriminatorColumn": "thread_id", "textColumn": "body",
                "directionColumn": "direction", "directionSentValue": "Sent", "timeColumn": "timestamp",
                "senderColumn": "from", "sentMessageStaticLabel": "Local User", "mediaColumn": "media"}}
            }],
            "Contacts": [{
              "artifact_key": "get_contacts", "name": "Contacts", "tablename": "get_contacts", "module": "contacts",
              "column_map": {"display_name": "Display Name", "phone": "Phone Number", "photo": "Photo"},
              "record_count": 3,
              "object_columns": [{"name": "photo", "type": "media"}]
            }],
            "Locations": [{
              "artifact_key": "get_trips", "name": "Trips", "tablename": "get_trips", "module": "maps",
              "column_map": {"start": "Start", "latitude": "Latitude", "longitude": "Longitude",
                             "to_latitude": "To Latitude", "to_longitude": "To Longitude", "long_name": "Long Name"},
              "record_count": 3,
              "object_columns": [{"name": "start", "type": "datetime"}]
            }]
          }
        }
        """);

        using (var con = new SqliteConnection($"Data Source={Path.Combine(Report, "_lava_artifacts.db")};Pooling=False"))
        {
            con.Open();
            Exec(con, """
                CREATE TABLE "get_chat" ("timestamp" INTEGER, "thread_id" TEXT, "body" TEXT, "direction" TEXT, "from" TEXT, "media" TEXT, "msg_id" TEXT, "account_id" TEXT);
                INSERT INTO get_chat VALUES (1714557600,'1','Hello 100% sure','Received','+393331111111',NULL,'501','acc1');
                INSERT INTO get_chat VALUES (1714557660,'1','=cmd|calc','Sent','+393332222222','ref1','502','acc1');
                INSERT INTO get_chat VALUES (1714557720,'2','photo pair','Received','+393333333333','["ref1","ref2"]','503','acc1');
                INSERT INTO get_chat VALUES (1717236000,'2','June note','Sent','+393332222222','ref_evil','504','acc1');
                CREATE TABLE "get_contacts" ("display_name" TEXT, "phone" TEXT, "photo" TEXT);
                INSERT INTO get_contacts VALUES ('Anna Example','333 111 1111','ref2');
                INSERT INTO get_contacts VALUES ('Dup One','+39 333 333 3333',NULL);
                INSERT INTO get_contacts VALUES ('Dup Two','3333333333',NULL);
                CREATE TABLE "get_trips" ("start" INTEGER, "latitude" TEXT, "longitude" TEXT, "to_latitude" TEXT, "to_longitude" TEXT, "long_name" TEXT);
                INSERT INTO get_trips VALUES (1714557600,'45.4642','9.19','41.9028','12.4964','Milan to Rome');
                INSERT INTO get_trips VALUES (1714557700,'0','0',NULL,NULL,'null island');
                INSERT INTO get_trips VALUES (1714557800,'abc','9.19','95','12','invalid');
                CREATE TABLE _lava_media_items (id TEXT PRIMARY KEY, source_path TEXT, extraction_path TEXT, type TEXT,
                  metadata TEXT, created_at INTEGER, updated_at INTEGER, is_embedded INTEGER);
                CREATE TABLE _lava_media_references (id TEXT PRIMARY KEY, media_item_id TEXT, module_name TEXT, artifact_name TEXT, name TEXT);
                CREATE VIEW _lava_media_info AS SELECT lmr.id as 'media_ref_id', lmr.media_item_id, lmr.module_name, lmr.artifact_name,
                  lmr.name, lmi.source_path, lmi.extraction_path, lmi.type, lmi.metadata, lmi.created_at, lmi.updated_at, lmi.is_embedded
                  FROM _lava_media_references as lmr LEFT JOIN _lava_media_items as lmi ON lmr.media_item_id = lmi.id;
                INSERT INTO _lava_media_items VALUES ('i1','/data/a.jpg','media/aaa.jpg','image/jpeg','',0,0,0);
                INSERT INTO _lava_media_items VALUES ('i2','/data/b.png','media/bbb.png','image/png','',1714557600000,1714557700,0);
                INSERT INTO _lava_media_items VALUES ('i3','/data/x','../../../../Windows/win.ini','text/plain','',0,0,0);
                INSERT INTO _lava_media_references VALUES ('ref1','i1','chat','Chat Messages','a.jpg');
                INSERT INTO _lava_media_references VALUES ('ref2','i2','chat','Chat Messages','b.png');
                INSERT INTO _lava_media_references VALUES ('ref_evil','i3','chat','Chat Messages','win.ini');
                """);
        }
        using (var tl = new SqliteConnection($"Data Source={Path.Combine(Report, "_Timeline", "tl.db")};Pooling=False"))
        {
            tl.Open();
            Exec(tl, """
                CREATE TABLE data(key TEXT, activity TEXT, datalist TEXT);
                INSERT INTO data VALUES ('2024-05-01 10:01:00+00:00','Chat Messages','{"Timestamp":"2024-05-01 10:01:00+00:00","Body":"=cmd|calc"}');
                INSERT INTO data VALUES ('2024-05-01 10:00:00+00:00','Chat Messages','{"Timestamp":"2024-05-01 10:00:00+00:00","Body":"Hello"}');
                INSERT INTO data VALUES ('2024-05-01 10:00:00+00:00','Trips','{"Start":"2024-05-01 10:00:00+00:00","Long Name":"Milan to Rome"}');
                """);
        }
    }

    static void Exec(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}
