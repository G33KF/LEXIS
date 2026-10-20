"""Android backup files (.ab), as ``adb backup`` writes them.

A text header, then the payload: AES-256 encrypted when a backup password was set,
deflated when compressed, and a tar inside. The format follows AOSP (Apache-2.0) at
commit 1cdfff555f4a21f71ccc978290e2e212e2f8b168 of platform_frameworks_base:
services/backup/java/com/android/server/backup/restore/PerformAdbRestoreTask.java
(header lines, key unwrap, checksum, version 1 fallback), .../backup/utils/PasswordUtils.java
(PBKDF2 key size and the byte-to-char checksum input), .../backup/BackupPasswordManager.java
(PBKDF2WithHmacSHA1, PBKDF2WithHmacSHA1And8bit) and core/java/android/app/backup/FullBackup.java
(the domain tokens below).

decode() writes the tar out as a plain tar whose member names say where each tree lives on
the device (apps/<pkg>/db/x -> data/data/<pkg>/databases/x), so the artifacts' paths find
them, and returns the original and new name of every member so the renaming is on record.
"""
import hashlib
import io
import tarfile
import zlib

from Crypto.Cipher import AES

MAGIC = b'ANDROID BACKUP\n'

# FullBackup.java domain tokens -> the tree on the device. User 0 is assumed: the .ab
# header does not record which user was backed up.
_APP_TREES = {
    'r': 'data/data/{}', 'f': 'data/data/{}/files', 'db': 'data/data/{}/databases',
    'sp': 'data/data/{}/shared_prefs', 'c': 'data/data/{}/cache', 'nb': 'data/data/{}/no_backup',
    'd_r': 'data/user_de/0/{}', 'd_f': 'data/user_de/0/{}/files', 'd_db': 'data/user_de/0/{}/databases',
    'd_sp': 'data/user_de/0/{}/shared_prefs', 'd_c': 'data/user_de/0/{}/cache',
    'd_nb': 'data/user_de/0/{}/no_backup',
    'ef': 'storage/emulated/0/Android/data/{}/files', 'obb': 'storage/emulated/0/Android/obb/{}',
    'a': 'data/app/{}',
}


class BackupError(ValueError):
    """The file is not an Android backup, or the password does not open it."""


def device_path(name):
    """Where a backup member lives on the device; names outside the known domains are kept."""
    parts = name.split('/')
    if len(parts) >= 3 and parts[0] == 'apps' and parts[2] in _APP_TREES:
        return '/'.join([_APP_TREES[parts[2]].format(parts[1])] + parts[3:])
    if len(parts) >= 2 and parts[0] == 'shared':
        rest = parts[1:] if parts[1].isdigit() else ['0'] + parts[1:]
        return '/'.join(['storage/emulated'] + rest)
    return name                                 # apps/<pkg>/_manifest, apps/<pkg>/k/..., anything else


def read_header(fp):
    """The header as a dict, the file positioned at the payload."""
    if fp.read(len(MAGIC)) != MAGIC:
        raise BackupError('not an Android backup: the file does not start with "ANDROID BACKUP"')

    def line():
        return fp.readline(4096).rstrip(b'\n').decode('ascii', 'replace')

    try:
        header = {'version': int(line()), 'compressed': int(line()) != 0, 'encryption': line()}
        if header['encryption'] == 'AES-256':
            header.update(user_salt=bytes.fromhex(line()), checksum_salt=bytes.fromhex(line()),
                          rounds=int(line()), user_iv=bytes.fromhex(line()), key_blob=bytes.fromhex(line()))
    except ValueError as ex:
        raise BackupError(f'malformed Android backup header ({ex})') from ex
    if header['encryption'] not in ('none', 'AES-256'):
        raise BackupError(f'unknown backup encryption "{header["encryption"]}"')
    return header


def _unpad(data):
    n = data[-1] if data else 0
    return data[:-n] if 1 <= n <= 16 and data.endswith(bytes([n]) * n) else None


def _java_chars_utf8(data):
    """PasswordUtils.makeKeyChecksum casts each signed byte to a char (0x80..0xFF become
    0xFF80..0xFFFF), and PBKDF2WithHmacSHA1 takes the chars as UTF-8."""
    return ''.join(chr(b | 0xFF00 if b >= 0x80 else b) for b in data).encode('utf-8')


def unlock(header, password):
    """(payload key, payload IV) for an encrypted backup. Raises BackupError for a wrong password."""
    if isinstance(password, bytes):
        password = password.decode('utf-8', 'surrogateescape')
    attempts = [(password.encode('utf-8', 'surrogateescape'), _java_chars_utf8)]
    if header['version'] == 1:                  # PBKDF2WithHmacSHA1And8bit: the low byte of each char
        attempts.append((bytes(ord(c) & 0xFF for c in password), bytes))
    for pw, checksum_input in attempts:
        user_key = hashlib.pbkdf2_hmac('sha1', pw, header['user_salt'], header['rounds'], 32)
        blob = _unpad(AES.new(user_key, AES.MODE_CBC, header['user_iv']).decrypt(header['key_blob']))
        if not blob:
            continue
        fields, i = [], 0
        while len(fields) < 3 and i < len(blob):
            fields.append(blob[i + 1:i + 1 + blob[i]])
            i += 1 + blob[i]
        if len(fields) < 3:
            continue
        iv, key, checksum = fields[0], fields[1], fields[2]
        if hashlib.pbkdf2_hmac('sha1', checksum_input(key), header['checksum_salt'], header['rounds'], 32) == checksum:
            return key, iv
    raise BackupError('the password does not open this Android backup')


class _Payload(io.RawIOBase):
    """The payload decrypted and inflated, read front to back."""

    def __init__(self, fp, header, password):
        super().__init__()
        self._fp, self._buf, self._done, self._cipher = fp, b'', False, None
        if header['encryption'] == 'AES-256':
            key, iv = unlock(header, password)
            self._cipher = AES.new(key, AES.MODE_CBC, iv)
        self._held = b''                         # the last cipher block, unpadded only at the end
        self._inflate = zlib.decompressobj() if header['compressed'] else None
        self.truncated = False

    def readable(self):
        return True

    def _next(self):
        raw = self._fp.read(1 << 20)
        if self._cipher:
            data = self._held + raw
            cut = len(data) - 16 if raw else len(data)
            cut -= cut % 16
            plain, self._held = self._cipher.decrypt(data[:cut]), data[cut:]
            if not raw:
                if self._held:                  # a ciphertext that does not end on a block
                    self.truncated = True
                plain = _unpad(plain) if plain else plain
                if plain is None:
                    self.truncated, plain = True, b''
        else:
            plain = raw
        if self._inflate:
            plain = self._inflate.decompress(plain)
            if not raw:
                plain += self._inflate.flush()
                self.truncated |= not self._inflate.eof
        return plain, not raw

    def readinto(self, b):
        while not self._buf and not self._done:
            self._buf, self._done = self._next()
        n = min(len(b), len(self._buf))
        b[:n], self._buf = self._buf[:n], self._buf[n:]
        return n


def decode(ab_path, out_tar, password=None):
    """Write the backup's files to out_tar under their device paths.

    Returns (header, renamed, truncated): renamed lists (name in the backup, name written)
    for every member, and truncated says the payload ended early, which an interrupted
    ``adb backup`` leaves; what was read up to there is kept.
    """
    renamed, truncated = [], False
    with open(ab_path, 'rb') as fp:
        header = read_header(fp)
        if header['encryption'] == 'AES-256' and not password:
            raise BackupError('this Android backup is encrypted; give its password with '
                              '--image_password_file or --image_password_env')
        payload = _Payload(fp, header, password)
        with tarfile.open(out_tar, 'w', format=tarfile.PAX_FORMAT) as dst:
            try:
                src = tarfile.open(fileobj=io.BufferedReader(payload, 1 << 20), mode='r|')
                for member in src:
                    if member.name.startswith('/') or '..' in member.name.split('/'):
                        renamed.append((member.name, ''))
                        continue
                    original, member.name = member.name, device_path(member.name)
                    renamed.append((original, member.name))
                    dst.addfile(member, src.extractfile(member) if member.isreg() else None)
            except (tarfile.ReadError, EOFError, zlib.error):
                truncated = True
        truncated |= payload.truncated
    return header, renamed, truncated


def _self_check():
    # Self-check: build an encrypted, compressed backup the way PerformAdbBackupTask does, decode it.
    import os
    import tempfile
    pw, rounds = 'pässword', 10000
    user_salt, ck_salt, user_iv, mk, mk_iv = (os.urandom(n) for n in (64, 64, 16, 32, 16))
    ck = hashlib.pbkdf2_hmac('sha1', _java_chars_utf8(mk), ck_salt, rounds, 32)
    blob = bytes([16]) + mk_iv + bytes([32]) + mk + bytes([32]) + ck
    pad = lambda d: d + bytes([16 - len(d) % 16]) * (16 - len(d) % 16)  # noqa: E731
    user_key = hashlib.pbkdf2_hmac('sha1', pw.encode(), user_salt, rounds, 32)
    tar = io.BytesIO()
    with tarfile.open(fileobj=tar, mode='w') as t:
        for name, body in (('apps/com.x/db/m.db', b'SQLite format 3\0'), ('apps/com.x/_manifest', b'1\ncom.x\n'),
                           ('shared/0/DCIM/a.jpg', b'\xff\xd8'), ('apps/com.x/../../evil', b'x')):
            info = tarfile.TarInfo(name)
            info.size = len(body)
            t.addfile(info, io.BytesIO(body))
    body = AES.new(mk, AES.MODE_CBC, mk_iv).encrypt(pad(zlib.compress(tar.getvalue())))
    head = (MAGIC + b'5\n1\nAES-256\n' + b'\n'.join(x.hex().upper().encode() for x in (user_salt, ck_salt))
            + f'\n{rounds}\n'.encode() + user_iv.hex().upper().encode() + b'\n'
            + AES.new(user_key, AES.MODE_CBC, user_iv).encrypt(pad(blob)).hex().upper().encode() + b'\n')
    d = tempfile.mkdtemp()
    ab, out = os.path.join(d, 'b.ab'), os.path.join(d, 'o.tar')
    with open(ab, 'wb') as f:
        f.write(head + body)
    try:
        decode(ab, out, 'wrong')
        raise AssertionError('wrong password accepted')
    except BackupError:
        pass
    h, names, cut = decode(ab, out, pw.encode())
    with tarfile.open(out) as t:
        assert t.getnames() == ['data/data/com.x/databases/m.db', 'apps/com.x/_manifest',
                                'storage/emulated/0/DCIM/a.jpg'], t.getnames()
        assert t.extractfile('data/data/com.x/databases/m.db').read() == b'SQLite format 3\0'
    assert names[-1] == ('apps/com.x/../../evil', '') and not cut and h['version'] == 5
    with open(ab, 'wb') as f:                    # interrupted backup: keep what arrived
        f.write(head + body[:len(body) // 2])
    assert decode(ab, out, pw)[2]
    print('ok')


if __name__ == '__main__':
    _self_check()
