/**
 * Unit tests for validateExtField — L4 PR review finding.
 * Tests all 10 field-specific validation branches in extension-field-validator.ts.
 */
import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { validateExtField } from '../manifest-editor/extension-field-validator';

describe('validateExtField', () => {

    // ─── Required field checks ─────────────────────────────────

    describe('required field handling', () => {
        it('returns error when required field is empty', () => {
            const result = validateExtField('Protocol.Name', '', true);
            assert.equal(result?.level, 'error');
            assert.ok(result?.message.includes('required'));
        });

        it('returns null when optional field is empty', () => {
            assert.equal(validateExtField('Protocol.Name', '', false), null);
        });

        it('returns null for unknown field with valid value', () => {
            assert.equal(validateExtField('SomeUnknown.Field', 'anything', false), null);
        });
    });

    // ─── GUID fields ───────────────────────────────────────────

    describe('Class.Id (GUID validation)', () => {
        it('accepts valid GUID with braces', () => {
            assert.equal(validateExtField('Class.Id', '{12345678-1234-1234-1234-123456789012}', false), null);
        });

        it('accepts valid GUID without braces', () => {
            assert.equal(validateExtField('Class.Id', '12345678-1234-1234-1234-123456789012', false), null);
        });

        it('rejects invalid GUID', () => {
            const result = validateExtField('Class.Id', 'not-a-guid', false);
            assert.equal(result?.level, 'error');
            assert.ok(result?.message.includes('GUID'));
        });
    });

    describe('ToastNotificationActivation.ToastActivatorCLSID', () => {
        it('accepts valid GUID', () => {
            assert.equal(validateExtField('ToastNotificationActivation.ToastActivatorCLSID', '{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}', false), null);
        });

        it('rejects invalid GUID', () => {
            const result = validateExtField('ToastNotificationActivation.ToastActivatorCLSID', 'bad', false);
            assert.equal(result?.level, 'error');
        });
    });

    // ─── ExecutionAlias.Alias ──────────────────────────────────

    describe('ExecutionAlias.Alias', () => {
        it('accepts valid alias ending with .exe', () => {
            assert.equal(validateExtField('ExecutionAlias.Alias', 'myapp.exe', false), null);
        });

        it('rejects alias not ending with .exe', () => {
            const result = validateExtField('ExecutionAlias.Alias', 'myapp', false);
            assert.equal(result?.level, 'error');
            assert.ok(result?.message.includes('.exe'));
        });

        it('rejects alias with path separators', () => {
            const result = validateExtField('ExecutionAlias.Alias', 'path\\app.exe', false);
            assert.equal(result?.level, 'error');
            assert.ok(result?.message.includes('special characters'));
        });

        it('rejects alias with special characters', () => {
            const result = validateExtField('ExecutionAlias.Alias', 'my*app.exe', false);
            assert.equal(result?.level, 'error');
        });
    });

    // ─── Protocol.Name ─────────────────────────────────────────

    describe('Protocol.Name', () => {
        it('accepts valid protocol name', () => {
            assert.equal(validateExtField('Protocol.Name', 'myapp', false), null);
        });

        it('accepts protocol with dots, plus, hyphen', () => {
            assert.equal(validateExtField('Protocol.Name', 'my.app+v2-beta', false), null);
        });

        it('rejects protocol starting with digit', () => {
            const result = validateExtField('Protocol.Name', '1protocol', false);
            assert.equal(result?.level, 'error');
            assert.ok(result?.message.includes('lowercase letter'));
        });

        it('rejects uppercase protocol name', () => {
            const result = validateExtField('Protocol.Name', 'MyApp', false);
            assert.equal(result?.level, 'error');
        });
    });

    // ─── FileType ──────────────────────────────────────────────

    describe('FileType', () => {
        it('accepts valid file extension', () => {
            assert.equal(validateExtField('FileType', '.txt', false), null);
        });

        it('rejects extension without leading dot', () => {
            const result = validateExtField('FileType', 'txt', false);
            assert.equal(result?.level, 'error');
            assert.ok(result?.message.includes('.'));
        });

        it('rejects extension with special characters', () => {
            const result = validateExtField('FileType', '.tx-t', false);
            assert.equal(result?.level, 'error');
        });
    });

    // ─── FileTypeAssociation.Name ──────────────────────────────

    describe('FileTypeAssociation.Name', () => {
        it('accepts valid name', () => {
            assert.equal(validateExtField('FileTypeAssociation.Name', 'myfiletype', false), null);
        });

        it('accepts name with dots and digits', () => {
            assert.equal(validateExtField('FileTypeAssociation.Name', 'my.file.type1', false), null);
        });

        it('rejects name with special characters', () => {
            const result = validateExtField('FileTypeAssociation.Name', 'my-file', false);
            assert.equal(result?.level, 'error');
            assert.ok(result?.message.includes('letters, digits'));
        });
    });

    // ─── StartupTask.Enabled ───────────────────────────────────

    describe('StartupTask.Enabled', () => {
        it('accepts "true"', () => {
            assert.equal(validateExtField('StartupTask.Enabled', 'true', false), null);
        });

        it('accepts "false"', () => {
            assert.equal(validateExtField('StartupTask.Enabled', 'false', false), null);
        });

        it('rejects other values', () => {
            const result = validateExtField('StartupTask.Enabled', 'yes', false);
            assert.equal(result?.level, 'error');
            assert.ok(result?.message.includes('"true" or "false"'));
        });
    });

    // ─── ExeServer.Executable (warning) ────────────────────────

    describe('ExeServer.Executable', () => {
        it('accepts .exe path', () => {
            assert.equal(validateExtField('ExeServer.Executable', 'myserver.exe', false), null);
        });

        it('accepts .dll path', () => {
            assert.equal(validateExtField('ExeServer.Executable', 'mylib.dll', false), null);
        });

        it('warns for non .exe/.dll path', () => {
            const result = validateExtField('ExeServer.Executable', 'myserver.bat', false);
            assert.equal(result?.level, 'warning');
            assert.ok(result?.message.includes('.exe or .dll'));
        });
    });

    // ─── Task.Type (warning) ───────────────────────────────────

    describe('Task.Type', () => {
        it('accepts known type "timer"', () => {
            assert.equal(validateExtField('Task.Type', 'timer', false), null);
        });

        it('accepts known type "pushNotification"', () => {
            assert.equal(validateExtField('Task.Type', 'pushNotification', false), null);
        });

        it('warns for unknown type', () => {
            const result = validateExtField('Task.Type', 'unknownType', false);
            assert.equal(result?.level, 'warning');
            assert.ok(result?.message.includes('Common values'));
        });
    });

    // ─── AppService.Name (warning) ─────────────────────────────

    describe('AppService.Name', () => {
        it('accepts valid reverse-domain name', () => {
            assert.equal(validateExtField('AppService.Name', 'com.contoso.myservice', false), null);
        });

        it('warns for name starting with digit', () => {
            const result = validateExtField('AppService.Name', '1service', false);
            assert.equal(result?.level, 'warning');
            assert.ok(result?.message.includes('reverse-domain'));
        });

        it('warns for name with hyphens', () => {
            const result = validateExtField('AppService.Name', 'my-service', false);
            assert.equal(result?.level, 'warning');
        });
    });
});
