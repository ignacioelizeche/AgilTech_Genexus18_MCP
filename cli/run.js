#!/usr/bin/env node
const { main, EXIT_CODES } = require('./index');
const { writeStructured } = require('./lib/output');
const { operationalErrorEnvelope } = require('./commands/axi');

main(process.argv.slice(2))
    .then((code) => process.exit(code))
    .catch((err) => {
        writeStructured(process.stdout, operationalErrorEnvelope(`Unhandled CLI failure: ${err.message}`, EXIT_CODES.ERROR), 'toon');
        process.exit(EXIT_CODES.ERROR);
    });
