"""Verify native load handoff with owned real controlling terminals."""
from __future__ import annotations
import argparse
from contextlib import suppress
import fcntl
import json
import os
from pathlib import Path
import select
import shlex
import signal
import struct
import subprocess
import tempfile
import termios
import time

parser = argparse.ArgumentParser()
parser.add_argument('binary')
parser.add_argument('--tmux', required=True)
parser.add_argument('--output', required=True)
parser.add_argument('--case', action='append')
args = parser.parse_args()
tmux = str(Path(args.tmux).resolve())
output = Path(args.output)
output.mkdir(parents=True, exist_ok=True)
binary = str(Path(args.binary).resolve())
cli = [os.environ.get('DOTNET_HOST_PATH', 'dotnet'), binary] if binary.endswith('.dll') else [binary]
base = Path('/tmp/libtmux-dotnet-test')
base.mkdir(exist_ok=True)

class Terminal:
    def __init__(self, command, environment, *, input_terminal=True, controlling=True, foreground=True):
        self.fd, slave = os.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 30, 100, 0, 0))
        self.before = termios.tcgetattr(slave)
        self.tty = os.ttyname(slave)
        self.content = bytearray()
        self.status = None
        self.pid = os.fork()
        if self.pid == 0:
            os.close(self.fd)
            if controlling:
                os.login_tty(slave)
            else:
                os.setsid()
                for descriptor in range(3): os.dup2(slave, descriptor)
                os.close(slave)
            if not foreground:
                child = os.fork()
                if child:
                    def stop(*_):
                        with suppress(ProcessLookupError): os.killpg(child, signal.SIGKILL)
                    signal.signal(signal.SIGTERM, stop)
                    _, status = os.waitpid(child, 0)
                    os._exit(os.waitstatus_to_exitcode(status) & 255)
                os.setpgid(0, 0)
            if not input_terminal:
                null = os.open('/dev/null', os.O_RDONLY)
                os.dup2(null, 0)
                os.close(null)
            os.execve(command[0], command, environment)
        os.close(slave)

    def pump(self):
        if select.select([self.fd], [], [], .01)[0]:
            with suppress(OSError):
                self.content += os.read(self.fd, 65536)
                self.content = self.content[-65536:]
        if self.status is None:
            ended, status = os.waitpid(self.pid, os.WNOHANG)
            if ended:
                self.status = os.waitstatus_to_exitcode(status)
                # Drain bytes already queued when the leader was reaped.
                while select.select([self.fd], [], [], 0)[0]:
                    try:
                        chunk = os.read(self.fd, 65536)
                    except OSError:
                        break
                    if not chunk:
                        break
                    self.content += chunk
                    self.content = self.content[-65536:]

    def until(self, condition, *, deadline=4):
        stop = time.monotonic() + deadline
        while time.monotonic() < stop:
            self.pump()
            if condition():
                return
            if self.status is not None:
                raise AssertionError(('terminal ended', self.status, bytes(self.content)))
        raise AssertionError(('fixture deadline', bytes(self.content)))

    def close(self):
        if self.status is None:
            with suppress(ProcessLookupError):
                os.kill(self.pid, signal.SIGTERM)
            stop = time.monotonic() + .5
            while self.status is None and time.monotonic() < stop:
                self.pump()
            if self.status is None:
                os.kill(self.pid, signal.SIGKILL)
                os.waitpid(self.pid, 0)
        os.close(self.fd)


def check(case):
    with tempfile.TemporaryDirectory(prefix='handoff-', dir=base) as temporary:
        root = Path(temporary)
        socket = str(root / 'tmux')
        environment = dict(os.environ, TERM='xterm-256color', HOME=str(root),
            XDG_CONFIG_HOME=str(root), ZDOTDIR=str(root), SHELL='/bin/sh',
            ENV='/dev/null', BASH_ENV='/dev/null', TMUX_TMPDIR=str(root),
            LIBTMUX_TMUX=tmux, DOTNET_PROCESSOR_COUNT='2', DOTNET_CLI_TELEMETRY_OPTOUT='1')
        environment.pop('TMUX', None)
        environment.pop('TMUX_PANE', None)
        terminals = []
        def command(*words, check=True):
            result = subprocess.run([tmux, '-f', '/dev/null', '-S', socket, *words],
                env=environment, capture_output=True, text=True, timeout=3, check=check)
            return result.stdout.strip()
        def clients():
            return [row.split('\t') for row in command('list-clients', '-F',
                '#{client_name}\t#{client_pid}\t#{session_name}\t#{pane_id}\t#{client_flags}', check=case!='outside-generation').splitlines()]
        def sessions():
            return command('list-sessions', '-F', '#{session_name}').splitlines()
        def terminal(words, **kwargs):
            item = Terminal(words, environment, **kwargs)
            terminals.append(item)
            return item
        marker = root / 'script-ran'
        release = root / 'release'
        before_script = root / 'before.sh'
        script = 'printf ran > ' + shlex.quote(str(marker)) + '\n'
        if case in ['late-client-move', 'late-independent']:
            script += 'while [ ! -e ' + shlex.quote(str(release)) + ' ]; do sleep 0.01; done\n'
        if case == 'script-replaces-daemon-fails':
            script += shlex.join([tmux, '-S', socket, 'kill-server']) + '\n'
            script += shlex.join([tmux, '-f', '/dev/null', '-S', socket, 'new-session', '-d', '-s', 'replacement-keeper', '/bin/sh']) + '\n'
            script += shlex.join([tmux, '-S', socket, 'new-session', '-d', '-s', 'replacement', '/bin/sh']) + '\n'
            script += shlex.join([tmux, '-S', socket, 'list-sessions', '-F', '#{session_id}\t#{session_name}\t#{pid}:#{start_time}']) + ' > ' + shlex.quote(str(root / 'replacement-identity')) + '\nexit 7\n'
        before_script.write_text(script)
        config = root / 'workspace.json'
        document = {'session_name':'loaded', 'before_script':shlex.join(['/bin/sh', str(before_script)]),
                    'windows':[{'panes':[None]}]}
        if case in ['extension-nonterminal','choice-detached-extension']:
            document['plugins'] = ['fixture.Plugin']
            python = root / 'python'
            python.write_text('#!/bin/sh\nprintf invoked > ' + shlex.quote(str(root / 'python-ran')) + '\nexit 23\n')
            python.chmod(0o700)
            if case == 'choice-detached-extension':
                python.write_text('#!/usr/bin/python3\nimport json,sys\nfrom pathlib import Path\n'
                    'if any("importlib.metadata" in word for word in sys.argv): print("1.74.0")\n'
                    'else: Path(' + repr(str(root / 'python-argv')) + ').write_text(json.dumps(sys.argv[1:]))\n')
            environment['TMUX_WORKSPACE_PYTHON'] = str(python)
        if case == 'missing-client-tty':
            wrapper = root / 'tmux-wrapper'
            wrapper.write_text('#!/usr/bin/python3\nimport os,sys\n'
                'args=[value.replace("#{client_tty}", "") for value in sys.argv[1:]]\n'
                'os.execv(' + repr(tmux) + ', [' + repr(tmux) + ', *args])\n')
            wrapper.chmod(0o700)
            environment['LIBTMUX_TMUX'] = str(wrapper)
        if case == 'outside-generation':
            wrapper = root / 'tmux-wrapper'
            wrapper.write_text('#!/bin/sh\nfor argument in "$@"; do\n'
                'if [ "$argument" = attach-session ]; then\n'
                + shlex.join([tmux, '-S', socket, 'kill-server']) + '\n'
                + shlex.join([tmux, '-f', '/dev/null', '-S', socket, 'new-session', '-d', '-s', 'replacement-keeper', '/bin/sh']) + '\n'
                + shlex.join([tmux, '-S', socket, 'new-session', '-d', '-s', 'replacement', '/bin/sh']) + '\nfi\ndone\nexec '
                + shlex.quote(tmux) + ' "$@"\n')
            wrapper.chmod(0o700)
            environment['LIBTMUX_TMUX'] = str(wrapper)
        config.write_text(json.dumps(document))
        arguments = [*cli, '--color', 'never', 'load', str(config), '-S', socket, '-f', '/dev/null', '--no-progress']
        if case == 'script-replaces-daemon-fails': arguments.append('-d')
        result = {'case':case}
        try:
            pane = command('new-session', '-d', '-s', 'keeper', '-P', '-F', '#{pane_id}', '/bin/sh')
            command('set-option', '-g', 'default-shell', '/bin/sh')
            if case.startswith('outside-') or case in ['nonterminal-input', 'extension-nonterminal','script-replaces-daemon-fails','noncontrolling-terminal','background-terminal']:
                owner = terminal(arguments, input_terminal=case not in ['nonterminal-input', 'extension-nonterminal'], controlling=case!='noncontrolling-terminal', foreground=case!='background-terminal')
                if case.startswith('outside-'):
                    owner.until(lambda: owner.status is not None or any(row[2] in ['loaded', 'replacement'] for row in clients()))
                    result['attached'] = clients()
                    if case in ['outside-detach','outside-generation']:
                        if owner.status is None: os.write(owner.fd, b'\x02d')
                    else:
                        os.kill(owner.pid, signal.SIGINT if case == 'outside-int' else signal.SIGTERM)
                owner.until(lambda: owner.status is not None or (case in ['noncontrolling-terminal','background-terminal'] and marker.exists()))
                result.update(exit=owner.status, sessions=sessions(), clients=clients(), script_ran=marker.exists(),
                    python_ran=(root / 'python-ran').exists(), terminal_restored=termios.tcgetattr(owner.fd)==owner.before)
                if case == 'script-replaces-daemon-fails':
                    result['replacement_before_cleanup'] = (root / 'replacement-identity').read_text()
                    result['contract_pass'] = owner.status==1 and 'replacement' in result['sessions'] and b'before_script exited with status 7' in owner.content
                elif case == 'outside-detach':
                    result['contract_pass'] = owner.status == 0 and not result['clients'] and result['terminal_restored']
                elif case == 'outside-generation':
                    result['contract_pass'] = owner.status != 0 and not result['attached'] and result['terminal_restored']
                elif case.startswith('outside-'):
                    result['contract_pass'] = owner.status == 130 and not result['clients'] and result['terminal_restored'] and 'loaded' in result['sessions']
                else:
                    result['contract_pass'] = owner.status != 0 and result['sessions'] == ['keeper'] and not marker.exists() and not result['python_ran']
                result['terminal'] = owner.content.decode(errors='replace')
            else:
                attach = [tmux, '-S', socket, 'attach-session', '-t', '=keeper']
                other_pane = None
                if case == 'independent-pane':
                    other_pane = command('split-window', '-d', '-t', pane, '-P', '-F', '#{pane_id}', '/bin/sh')
                    attach += ['-f', 'active-pane']
                viewer = terminal(attach)
                viewer.until(lambda: len(clients())==1)
                selected = clients()[0][0]
                if case in ['ambiguous-yes','ambiguous-prompt']:
                    second = terminal([tmux, '-S', socket, 'attach-session', '-t', '=keeper'])
                    second.until(lambda: len(clients())==2)
                if case == 'independent-pane':
                    focus = root / 'focus'
                    os.write(viewer.fd, b'\x02o' + ('printf %s "$TMUX_PANE" > '+shlex.quote(str(focus))+'\n').encode())
                    viewer.until(lambda: focus.exists() and focus.read_text()==other_pane)
                    result['actual_input_pane'] = focus.read_text()
                    result['projected_pane'] = clients()[0][3]
                if case == 'spoofed-pane':
                    other = command('new-window', '-d', '-t', '=keeper:', '-P', '-F', '#{pane_id}', '/bin/sh')
                    arguments = ['/usr/bin/env', 'TMUX_PANE='+other, *arguments]
                if case == 'stale-pid':
                    inherited = command('display-message', '-p', '-t', pane, '#{pid},#{session_id}')
                    pid, session = inherited.split(',')
                    arguments = ['/usr/bin/env', 'TMUX='+socket+','+str(int(pid)+1)+','+session.removeprefix('$'), *arguments]
                if case == 'late-client-move':
                    command('new-session', '-d', '-s', 'elsewhere', '/bin/sh')
                exit_file = root / 'exit-code'
                prompted = case in ['choice-detached','choice-detached-extension','choice-append','choice-eof','ambiguous-prompt']
                line = shlex.join(arguments if prompted else [*arguments, '-y']) + '; printf "%s\\n" "$?" > ' + shlex.quote(str(exit_file))
                command('send-keys', '-t', pane, '-l', line)
                command('send-keys', '-t', pane, 'Enter')
                if prompted:
                    viewer.until(lambda: b'[y]' in viewer.content or exit_file.exists())
                    result['prompt_seen'] = b'[y]' in viewer.content
                    if result['prompt_seen']:
                        answer = {'choice-detached':b'n\n','choice-detached-extension':b'n\n','choice-append':b'a\n','choice-eof':b'\x04'}.get(case,b'y\n')
                        os.write(viewer.fd, answer)
                        if case == 'ambiguous-prompt':
                            viewer.until(lambda: b'Choose client:' in viewer.content)
                            selected = min(row[0] for row in clients())
                            os.write(viewer.fd, b'1\n')
                if case == 'late-independent':
                    viewer.until(marker.exists)
                    command('refresh-client', '-t', selected, '-f', 'active-pane')
                    release.touch()
                if case == 'late-client-move':
                    viewer.until(marker.exists)
                    command('switch-client', '-c', selected, '-t', '=elsewhere')
                    release.touch()
                viewer.until(lambda: exit_file.exists() and exit_file.read_text().endswith('\n'))
                status = int(exit_file.read_text())
                result.update(exit=status, sessions=sessions(), clients=clients(), script_ran=marker.exists(),
                    pane=command('capture-pane', '-p', '-t', pane))
                if case in ['unique-client','ambiguous-prompt']:
                    result['contract_pass'] = status==0 and any(row[0]==selected and row[2]=='loaded' for row in result['clients'])
                elif case == 'choice-detached-extension':
                    result['bridge_argv'] = json.loads((root / 'python-argv').read_text())
                    result['contract_pass'] = status==0 and '-d' in result['bridge_argv'] and result['sessions']==['keeper'] and all(row[2]=='keeper' for row in result['clients'])
                elif case == 'choice-detached':
                    result['contract_pass'] = status==0 and 'loaded' in result['sessions'] and all(row[2]=='keeper' for row in result['clients'])
                elif case == 'choice-append':
                    result['contract_pass'] = status==0 and result['sessions']==['keeper'] and marker.exists()
                elif case == 'late-independent':
                    result['contract_pass'] = status!=0 and 'loaded' in result['sessions'] and all(row[2]=='keeper' for row in result['clients'])
                elif case == 'late-client-move':
                    result['contract_pass'] = status!=0 and 'loaded' in result['sessions'] and any(row[0]==selected and row[2]=='elsewhere' for row in result['clients']) and 'Recorded load results:' in result['pane']
                else:
                    result['contract_pass'] = status!=0 and result['sessions']==['keeper'] and not marker.exists()
                if prompted: result['contract_pass'] = result['contract_pass'] and result['prompt_seen']
            return result
        finally:
            release.touch(exist_ok=True)
            command('kill-server', check=False)
            for item in reversed(terminals):
                item.close()

cases = args.case or ['outside-detach','outside-int','outside-term','nonterminal-input',
    'extension-nonterminal','noncontrolling-terminal','background-terminal','unique-client','ambiguous-yes','spoofed-pane','stale-pid',
    'independent-pane','script-replaces-daemon-fails','missing-client-tty','late-client-move','late-independent','outside-generation',
    'choice-detached','choice-detached-extension','choice-append','choice-eof','ambiguous-prompt']
rows=[]
for case in cases:
    started=time.monotonic()
    try:
        row=check(case)
    except Exception as failure:
        row={'case':case,'fixture_error':repr(failure)}
    row['seconds']=time.monotonic()-started
    rows.append(row)
    (output/(case+'.json')).write_text(json.dumps(row,indent=2)+'\n')
    print(case+': '+('FIXTURE ERROR' if 'fixture_error' in row else 'PASS' if row['contract_pass'] else 'RED'), flush=True)
(output/'summary.json').write_text(json.dumps({
    'tmux':tmux,'tmux_version':subprocess.check_output([tmux,'-V'],text=True).strip(),'cases':rows},indent=2)+'\n')
raise SystemExit(1 if any('fixture_error' in row or not row['contract_pass'] for row in rows) else 0)
