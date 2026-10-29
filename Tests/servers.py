"""Loopback-only FTP/SFTP integration fixtures; never serves user directories."""
import sys, pathlib, os, socket, threading, json, time, base64, hashlib
BASE = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(BASE / 'artifacts' / 'test-libs'))
import paramiko
from pyftpdlib.authorizers import DummyAuthorizer
from pyftpdlib.handlers import FTPHandler
from pyftpdlib.servers import FTPServer
ROOT = (BASE / 'artifacts' / 'test-server').resolve()
ROOT.mkdir(parents=True, exist_ok=True)
(ROOT / 'seed.bin').write_bytes(bytes(range(256)) * 16384)
MANY = ROOT / 'many'; MANY.mkdir(exist_ok=True)
for i in range(5000): (MANY / f'item-{i:05}.txt').touch()

auth = DummyAuthorizer(); auth.add_user('portly-test', 'local-test-only', str(ROOT), perm='elradfmwMT')
class Handler(FTPHandler):
    authorizer = auth
    banner = 'FileTransit loopback fixture'
ftp = FTPServer(('127.0.0.1', 0), Handler)
threading.Thread(target=ftp.serve_forever, kwargs={'timeout': .1}, daemon=True).start()

key = paramiko.RSAKey.generate(2048)
class Auth(paramiko.ServerInterface):
    def check_auth_password(self, user, password):
        return paramiko.AUTH_SUCCESSFUL if (user, password) == ('portly-test', 'local-test-only') else paramiko.AUTH_FAILED
    def get_allowed_auths(self, username): return 'password'
    def check_channel_request(self, kind, chanid): return paramiko.OPEN_SUCCEEDED if kind == 'session' else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

class Handle(paramiko.SFTPHandle):
    def stat(self):
        return paramiko.SFTPAttributes.from_stat(os.fstat(self.readfile.fileno()))

class SFTP(paramiko.SFTPServerInterface):
    def real(self, path):
        result = (ROOT / path.lstrip('/')).resolve()
        if result != ROOT and ROOT not in result.parents: raise PermissionError('Outside fixture')
        return result
    def list_folder(self, path):
        try:
            result = []
            for p in self.real(path).iterdir():
                a = paramiko.SFTPAttributes.from_stat(p.stat()); a.filename = p.name; result.append(a)
            return result
        except OSError as e: return paramiko.SFTPServer.convert_errno(e.errno or 13)
    def stat(self, path):
        try: return paramiko.SFTPAttributes.from_stat(self.real(path).stat())
        except OSError as e: return paramiko.SFTPServer.convert_errno(e.errno or 13)
    lstat = stat
    def open(self, path, flags, attr):
        try:
            fd = os.open(self.real(path), flags | getattr(os, 'O_BINARY', 0), 0o666)
            mode = 'r+b' if flags & os.O_RDWR else 'wb' if flags & os.O_WRONLY else 'rb'
            stream = os.fdopen(fd, mode)
            h = Handle(flags); h.readfile = stream; h.writefile = stream; return h
        except OSError as e: return paramiko.SFTPServer.convert_errno(e.errno or 13)
    def mkdir(self, path, attr):
        try: self.real(path).mkdir(); return paramiko.SFTP_OK
        except OSError as e: return paramiko.SFTPServer.convert_errno(e.errno or 13)
    def remove(self, path):
        try: self.real(path).unlink(); return paramiko.SFTP_OK
        except OSError as e: return paramiko.SFTPServer.convert_errno(e.errno or 13)
    def rmdir(self, path):
        try: self.real(path).rmdir(); return paramiko.SFTP_OK
        except OSError as e: return paramiko.SFTPServer.convert_errno(e.errno or 13)
    def rename(self, old, new):
        try: self.real(old).rename(self.real(new)); return paramiko.SFTP_OK
        except OSError as e: return paramiko.SFTPServer.convert_errno(e.errno or 13)

sock = socket.socket(); sock.bind(('127.0.0.1', 0)); sock.listen(20)
def serve(client):
    transport = paramiko.Transport(client); transport.add_server_key(key)
    transport.set_subsystem_handler('sftp', paramiko.SFTPServer, SFTP)
    try:
        transport.start_server(server=Auth())
        while transport.is_active(): time.sleep(.1)
    except Exception: pass
    finally: transport.close()
def accept():
    while True:
        client, address = sock.accept(); threading.Thread(target=serve, args=(client,), daemon=True).start()
threading.Thread(target=accept, daemon=True).start()
settings = {'ftp': ftp.socket.getsockname()[1], 'sftp': sock.getsockname()[1], 'fingerprint': 'SHA256:' + base64.b64encode(hashlib.sha256(key.asbytes()).digest()).decode().rstrip('=')}
(BASE / 'artifacts' / 'test-servers.json').write_text(json.dumps(settings))
print('Loopback fixtures ready', flush=True)
try:
    while True: time.sleep(1)
except KeyboardInterrupt: pass
finally: ftp.close_all(); sock.close()
