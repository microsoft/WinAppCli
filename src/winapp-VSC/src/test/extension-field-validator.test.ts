/**
 * Unit tests for validateExtensionField — L4 PR review finding.
 * Tests all 10 field-specific validation branches in manifest-validator.ts.
 */
import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { validateExtensionField } from '../manifest-editor/manifest-validator';

describe('validateExtensionField', () => {

    // ─── Required field checks ─────────────────────────────────

    describe('required field handling', () => {
        it('returns error when required field is empty', () => {
            const result = validateExtensionField('Protocol.Name', '', true);
            assert.equal(result?.level, 'error');
            assert.ok(result?.message.includes('required'));
        });

        it('returns null when optional field is empty', () => {
            assert.equal(validateExtensionField('Protocol.Name', '', false), null);
        });

        it('returns null for unknown field with valid value', () => {
            assert.equal(validateExtensionField('SomeUnknown.Field', 'anything', false), null);
        });
    });

    // ─── GUID fields ───────────────────────────────────────────

    describe('Class.Id (GUID validation)', () => {
        it('accepts valid GUID with braces', () => {
            assert.equal(validateExtensionField('Class.Id', '{12345678-1234-1234-1234-123456789012}', false), null);
        });

        it('accepts valid GUID without braces', () => {
            assert.equal(validateExtensionField('Class.Id', '12345678-1234-1234-1234-123456789012', false), null);
        });

        it('rejects invalid GUID', () => {
            const result = validateExtensionField('Class.Id', 'not-a-guid', false);
            assert.equal(result?.level, 'error');
            assert.ok(result?.message.includes('GUID'));
        });
    });

    describe('ToastNotificationActivation.ToastActivatorCLSID', () => {
        it('accepts valid GUID', () => {
            assert.equal(validateExtensionField('ToastNotificationActivation.ToastActivatorCLSID', '{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}', false), null);
        });

        it('rejects invalid GUID', () => {
            const result = validateExtensionField('ToastNotificationActivation.ToastActivatorCLSID', 'bad', false);
            assert.equal(result?.level, 'error');
        });
    });

    // ─── ExecutionAlias.Alias ──────────────────────────────────

    describe('ExecutionAlias.Alias', () => {
        it('accepts valid alias ending with .exe', () => {
            assert.equal(validateExtensionField('ExecutionAlias.Alias', 'myapp.exe', false), null);
        });

        it('rejects alias not ending with .exe', () => {
            const result = validateExtensionField('ExecutionAlias.Alias', 'myapp', false);
            assert.equal(result?.level, 'error');
            assert.ok(result?.message.includes('.exe'));
        });

        it('rejects alias with path separators', () => {
            const result = validateExtensionField('ExecutionAlias.Alias', 'path\\app.exe', false);
            assert.equal(result?.level, 'error');
            assert.ok(result?.message.includes('special characters'));
        });

        it('rejects alias with special characters', () => {
            const result = validateExtensionField('ExecutionAlias.Alias', 'my*app.exe', false);
            assert.equal(result?.level, 'error');
        });
    });

    // ─── Protocol.Name ─────────────────────────────────────────

    describe('Protocol.Name', () => {
        it('accepts valid protocol name', () => {
            assert.equal(validateExtensionField('Protocol.Name', 'myapp', false), null);
        });

        it('accepts protocol with dots, plus, hyphen', () => {
            assert.equal(validateExtensionField('Protocol.Name', 'my.app+v2-beta', false), null);
        });

        it('rejects protocol starting with digit', () => {
            const result = validateExtensionField('Protocol.Name', '1protocol', false);
            assert.equal(result?.level, 'error');
            assert.ok(result?.message.includes('lowercase letter'));
        });

        it('rejects uppercase protocol name', () => {
            const result = validateExtensionField('Protocol.Name', 'MyApp', false);
            assert.equal(result?.level, 'error');
        });
    });

    // ─── FileType ──────────────────────────────────────────────

    describe('FileType', () => {
        it('accepts valid file extension', () => {
            assert.equal(validateExtensionField('FileType', '.txt', false), null);
        });

        it('rejects extension without leading dot', () => {
            const result = validateExtensionField('FileType', 'txt', false);
            assert.equal(result?.level, 'error');
            assert.ok(result?.message.includes('.'));
        });

        it('rejects extension with special characters', () => {
            const result = validateExtensionField('FileType', '.tx-t', false);
            assert.equal(result?.level, 'error');
        });
    });

    // ─── FileTypeAssociation.Name ──────────────────────────────

    describe('FileTypeAssociation.Name', () => {
        it('accepts valid name', () => {
            assert.equal(validateExtensionField('FileTypeAssociation.Name', 'myfiletype', false), null);
        });

        it('accepts name with dots and digits', () => {
            assert.equal(validateExtensionField('FileTypeAssociation.Name', 'my.file.type1', false), null);
        });

        it('rejects name with special characters', () => {
            const result = validateExtensionField('FileTypeAssociation.Name', 'my-file', false);
            assert.equal(result?.level, 'error');
            assert.ok(result?.message.includes('letters, digits'));
        });
    });

    // ─── StartupTask.Enabled ───────────────────────────────────

    describe('StartupTask.Enabled', () => {
        it('accepts "true"', () => {
            assert.equal(validateExtensionField('StartupTask.Enabled', 'true', false), null);
        });

        it('accepts "false"', () => {
            assert.equal(validateExtensionField('StartupTask.Enabled', 'false', false), null);
        });

        it('rejects other values', () => {
            const result = validateExtensionField('StartupTask.Enabled', 'yes', false);
            assert.equal(result?.level, 'error');
            assert.ok(result?.message.includes('"true" or "false"'));
        });
    });

    // ─── ExeServer.Executable (warning) ────────────────────────

    describe('ExeServer.Executable', () => {
        it('accepts .exe path', () => {
            assert.equal(validateExtensionField('ExeServer.Executable', 'myserver.exe', false), null);
        });

        it('accepts .dll path', () => {
            assert.equal(validateExtensionField('ExeServer.Executable', 'mylib.dll', false), null);
        });

        it('warns for non .exe/.dll path', () => {
            const result = validateExtensionField('ExeServer.Executable', 'myserver.bat', false);
            assert.equal(result?.level, 'warning');
            assert.ok(result?.message.includes('.exe or .dll'));
        });
    });

    // ─── Task.Type (warning) ───────────────────────────────────

    describe('Task.Type', () => {
        it('accepts known type "timer"', () => {
            assert.equal(validateExtensionField('Task.Type', 'timer', false), null);
        });

        it('accepts known type "pushNotification"', () => {
            assert.equal(validateExtensionField('Task.Type', 'pushNotification', false), null);
        });

        it('warns for unknown type', () => {
            const result = validateExtensionField('Task.Type', 'unknownType', false);
            assert.equal(result?.level, 'warning');
            assert.ok(result?.message.includes('Common values'));
        });
    });

    // ─── AppService.Name (warning) ─────────────────────────────

    describe('AppService.Name', () => {
        it('accepts valid reverse-domain name', () => {
            assert.equal(validateExtensionField('AppService.Name', 'com.contoso.myservice', false), null);
        });

        it('warns for name starting with digit', () => {
            const result = validateExtensionField('AppService.Name', '1service', false);
            assert.equal(result?.level, 'warning');
            assert.ok(result?.message.includes('reverse-domain'));
        });

        it('warns for name with hyphens', () => {
            const result = validateExtensionField('AppService.Name', 'my-service', false);
            assert.equal(result?.level, 'warning');
        });
    });
});
