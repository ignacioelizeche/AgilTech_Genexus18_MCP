#!/usr/bin/env node
const { main, EXIT_CODES } = require('./index');
const { writeStructured } = require('./lib/output');
const { operationalErrorEnvelope } = require('./commands/axi');

main(process.argv.slice(2))
    .then((code) => process.exit(code))
    .catch(() => {
        const envelope = operationalErrorEnvelope('Unhandled CLI failure.', EXIT_CODES.ERROR);
        envelope.meta = { ...(envelope.meta || {}), command: 'runtime' };
        writeStructured(process.stdout, envelope, 'toon');
        process.exit(EXIT_CODES.ERROR);
    });
