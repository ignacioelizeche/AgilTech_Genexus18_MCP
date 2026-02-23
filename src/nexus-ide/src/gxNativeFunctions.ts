export interface GxFunction {
    name: string;
    description: string;
    parameters: string;
    returnType: string;
    example?: string;
    snippet?: string;
    paramDetails?: string[];
}

export const nativeFunctions: GxFunction[] = [
    {
        name: 'ServerNow',
        description: 'Returns the current date and time of the application server.',
        parameters: '()',
        returnType: 'DateTime',
        example: '&DateTime = ServerNow()',
        snippet: 'ServerNow()',
        paramDetails: []
    },
    {
        name: 'Now',
        description: 'Returns the current date and time of the local machine.',
        parameters: '()',
        returnType: 'DateTime',
        example: '&DateTime = Now()',
        snippet: 'Now()'
    },
    {
        name: 'Today',
        description: 'Returns the current date.',
        parameters: '()',
        returnType: 'Date',
        example: '&Date = Today()',
        snippet: 'Today()'
    },
    {
        name: 'NullValue',
        description: 'Returns the null value of a given expression or attribute.',
        parameters: '(expression)',
        returnType: 'Same as expression',
        example: '&Var = NullValue(&Var)',
        snippet: 'NullValue(${1:expression})',
        paramDetails: ['expression: The attribute or variable to get the null value for.']
    },
    {
        name: 'Iif',
        description: 'Returns one of two values depending on the result of a logical expression.',
        parameters: '(condition, value_if_true, value_if_false)',
        returnType: 'Same as value_if_true',
        example: '&Result = Iif(&Amount > 100, "High", "Low")',
        snippet: 'Iif(${1:condition}, ${2:true_value}, ${3:false_value})',
        paramDetails: [
            'condition: The logical expression to evaluate.',
            'value_if_true: The value to return if condition is True.',
            'value_if_false: The value to return if condition is False.'
        ]
    },
    {
        name: 'IsNull',
        description: 'Checks if a value is null.',
        parameters: '(expression)',
        returnType: 'Boolean',
        example: 'If IsNull(&Var) ... EndIf',
        snippet: 'IsNull(${1:expression})'
    },
    {
        name: 'IsEmpty',
        description: 'Checks if a value is empty (null or zero/blank).',
        parameters: '(expression)',
        returnType: 'Boolean',
        example: 'If IsEmpty(&Var) ... EndIf',
        snippet: 'IsEmpty(${1:expression})'
    },
    {
        name: 'Str',
        description: 'Converts a numeric value to a string.',
        parameters: '(number, length, decimals)',
        returnType: 'String',
        example: '&Str = Str(&Num, 10, 2)',
        snippet: 'Str(${1:number}, ${2:length}, ${3:decimals})'
    },
    {
        name: 'Val',
        description: 'Converts a string to a numeric value.',
        parameters: '(string)',
        returnType: 'Numeric',
        example: '&Num = Val(&Str)',
        snippet: 'Val(${1:string})'
    },
    {
        name: 'Lower',
        description: 'Converts a string to lowercase.',
        parameters: '(string)',
        returnType: 'String',
        example: '&Str = Lower("ABC")',
        snippet: 'Lower(${1:string})'
    },
    {
        name: 'Upper',
        description: 'Converts a string to uppercase.',
        parameters: '(string)',
        returnType: 'String',
        example: '&Str = Upper("abc")',
        snippet: 'Upper(${1:string})'
    },
    {
        name: 'Len',
        description: 'Returns the length of a string.',
        parameters: '(string)',
        returnType: 'Numeric',
        example: '&Len = Len(&Str)',
        snippet: 'Len(${1:string})'
    },
    {
        name: 'Substr',
        description: 'Returns a substring from a string.',
        parameters: '(string, start, length)',
        returnType: 'String',
        example: '&Sub = Substr(&Str, 1, 5)',
        snippet: 'Substr(${1:string}, ${2:start}, ${3:length})',
        paramDetails: [
            'string: The source string.',
            'start: The starting position (1-based).',
            'length: The number of characters to extract.'
        ]
    },
    {
        name: 'Replace',
        description: 'Replaces all occurrences of a string within another string.',
        parameters: '(source, find, replace)',
        returnType: 'String',
        example: '&NewStr = Replace(&OldStr, "apple", "orange")',
        snippet: 'Replace(${1:source}, ${2:find}, ${3:replace})'
    },
    {
        name: 'TAdd',
        description: 'Adds a number of seconds to a DateTime value.',
        parameters: '(datetime, seconds)',
        returnType: 'DateTime',
        example: '&NewDT = TAdd(&OldDT, 3600)',
        snippet: 'TAdd(${1:datetime}, ${2:seconds})'
    },
    {
        name: 'TDiff',
        description: 'Returns the difference in seconds between two DateTime values.',
        parameters: '(datetime1, datetime2)',
        returnType: 'Numeric',
        example: '&Secs = TDiff(&DT1, &DT2)',
        snippet: 'TDiff(${1:datetime1}, ${2:datetime2})'
    },
    {
        name: 'DAdd',
        description: 'Adds a number of days to a Date or DateTime value.',
        parameters: '(date, days)',
        returnType: 'Same as date',
        example: '&NewDate = DAdd(&OldDate, 7)',
        snippet: 'DAdd(${1:date}, ${2:days})'
    },
    {
        name: 'DDiff',
        description: 'Returns the difference in days between two Date or DateTime values.',
        parameters: '(date1, date2)',
        returnType: 'Numeric',
        example: '&Days = DDiff(&D1, &D2)',
        snippet: 'DDiff(${1:date1}, ${2:date2})'
    },
    {
        name: 'YMDtoD',
        description: 'Returns a Date value from Year, Month, and Day.',
        parameters: '(year, month, day)',
        returnType: 'Date',
        example: '&Date = YMDtoD(2023, 12, 25)',
        snippet: 'YMDtoD(${1:year}, ${2:month}, ${3:day})'
    },
    {
        name: 'Msg',
        description: 'Displays a message to the user.',
        parameters: '(message_string)',
        returnType: 'None',
        example: 'Msg("Operation successful")',
        snippet: 'Msg(${1:message})'
    }
];

export const keywords: GxFunction[] = [
    {
        name: 'For Each',
        description: 'Iterates through a set of records in the database.',
        parameters: '',
        returnType: '',
        snippet: 'For Each\n\twhere ${1:condition}\n\t${2:code}\nEndFor'
    },
    {
        name: 'Do Case',
        description: 'Executes one of several blocks of code based on conditions.',
        parameters: '',
        returnType: '',
        snippet: 'Do Case\n\tCase ${1:condition}\n\t\t${2:code}\n\tOtherwise\n\t\t${3:code}\nEndCase'
    },
    {
        name: 'If',
        description: 'Conditional execution block.',
        parameters: '',
        returnType: '',
        snippet: 'If ${1:condition}\n\t${2:code}\nElse\n\t${3:code}\nEndIf'
    },
    {
        name: 'Sub',
        description: 'Defines a subroutine.',
        parameters: '(\'Name\')',
        returnType: '',
        snippet: 'Sub \'${1:SubName}\'\n\t${2:code}\nEndSub'
    },
    {
        name: 'New',
        description: 'Creates a new record in a table.',
        parameters: '',
        returnType: '',
        snippet: 'New\n\t${1:Attribute} = ${2:Value}\nEndNew'
    },
    {
        name: 'For In',
        description: 'Iterates through a collection.',
        parameters: '(&Var in &Collection)',
        returnType: '',
        snippet: 'For ${1:&item} in ${2:&collection}\n\t${3:code}\nEndFor'
    }
];

export interface GxMethod {
    name: string;
    description: string;
    parameters: string;
    returnType: string;
    snippet?: string;
}

export const typeMethods: { [type: string]: GxMethod[] } = {
    'Numeric': [
        { name: 'ToString', description: 'Converts to string.', parameters: '()', returnType: 'Character', snippet: 'ToString()' },
        { name: 'FromString', description: 'Parses a string into a numeric value.', parameters: '(string)', returnType: 'Numeric', snippet: 'FromString(${1:string})' },
        { name: 'Round', description: 'Rounds the value.', parameters: '(decimals)', returnType: 'Numeric', snippet: 'Round(${1:0})' },
        { name: 'Truncate', description: 'Truncates decimals.', parameters: '(decimals)', returnType: 'Numeric', snippet: 'Truncate(${1:0})' },
        { name: 'IsEmpty', description: 'Checks if empty.', parameters: '()', returnType: 'Boolean', snippet: 'IsEmpty()' },
        { name: 'IsNull', description: 'Checks if null.', parameters: '()', returnType: 'Boolean', snippet: 'IsNull()' },
        { name: 'SetEmpty', description: 'Sets to empty value.', parameters: '()', returnType: 'None', snippet: 'SetEmpty()' }
    ],
    'Character': [
        { name: 'Trim', description: 'Removes leading and trailing spaces.', parameters: '()', returnType: 'Character', snippet: 'Trim()' },
        { name: 'ToUpper', description: 'Converts to uppercase.', parameters: '()', returnType: 'Character', snippet: 'ToUpper()' },
        { name: 'ToLower', description: 'Converts to lowercase.', parameters: '()', returnType: 'Character', snippet: 'ToLower()' },
        { name: 'Length', description: 'Returns string length.', parameters: '()', returnType: 'Numeric', snippet: 'Length()' },
        { name: 'IndexOf', description: 'Finds substring position.', parameters: '(string)', returnType: 'Numeric', snippet: 'IndexOf(${1:string})' },
        { name: 'Replace', description: 'Replaces substrings.', parameters: '(find, replace)', returnType: 'Character', snippet: 'Replace(${1:find}, ${2:replace})' },
        { name: 'IsEmpty', description: 'Checks if empty.', parameters: '()', returnType: 'Boolean', snippet: 'IsEmpty()' },
        { name: 'SetEmpty', description: 'Sets to empty value.', parameters: '()', returnType: 'None', snippet: 'SetEmpty()' },
        { name: 'FromString', description: 'Parses a string into the current variable.', parameters: '(string)', returnType: 'None', snippet: 'FromString(${1:string})' },
        { name: 'CharAt', description: 'Returns the character at the specified index.', parameters: '(index)', returnType: 'Character', snippet: 'CharAt(${1:index})' },
        { name: 'Contains', description: 'Checks if the string contains a substring.', parameters: '(string)', returnType: 'Boolean', snippet: 'Contains(${1:string})' },
        { name: 'EndsWith', description: 'Checks if the string ends with a substring.', parameters: '(string)', returnType: 'Boolean', snippet: 'EndsWith(${1:string})' },
        { name: 'StartsWith', description: 'Checks if the string starts with a substring.', parameters: '(string)', returnType: 'Boolean', snippet: 'StartsWith(${1:string})' },
        { name: 'IsMatch', description: 'Checks if the string matches a regular expression.', parameters: '(regex)', returnType: 'Boolean', snippet: 'IsMatch("${1:regex}")' },
        { name: 'LastIndexOf', description: 'Returns the last index of a substring.', parameters: '(string)', returnType: 'Numeric', snippet: 'LastIndexOf(${1:string})' },
        { name: 'HTMLClean', description: 'Cleans HTML content.', parameters: '()', returnType: 'Character', snippet: 'HTMLClean()' }
    ],
    'Date': [
        { name: 'Year', description: 'Returns the year.', parameters: '()', returnType: 'Numeric', snippet: 'Year()' },
        { name: 'Month', description: 'Returns the month.', parameters: '()', returnType: 'Numeric', snippet: 'Month()' },
        { name: 'Day', description: 'Returns the day.', parameters: '()', returnType: 'Numeric', snippet: 'Day()' },
        { name: 'AddDays', description: 'Adds days.', parameters: '(days)', returnType: 'Date', snippet: 'AddDays(${1:days})' },
        { name: 'ToFormattedString', description: 'Converts to formatted string.', parameters: '()', returnType: 'Character', snippet: 'ToFormattedString()' },
        { name: 'IsEmpty', description: 'Checks if empty.', parameters: '()', returnType: 'Boolean', snippet: 'IsEmpty()' },
        { name: 'SetEmpty', description: 'Sets to empty value.', parameters: '()', returnType: 'None', snippet: 'SetEmpty()' }
    ],
    'DateTime': [
        { name: 'Hour', description: 'Returns the hour.', parameters: '()', returnType: 'Numeric', snippet: 'Hour()' },
        { name: 'Minute', description: 'Returns the minute.', parameters: '()', returnType: 'Numeric', snippet: 'Minute()' },
        { name: 'AddSeconds', description: 'Adds seconds.', parameters: '(seconds)', returnType: 'DateTime', snippet: 'AddSeconds(${1:seconds})' },
        { name: 'ToFormattedString', description: 'Converts to formatted string.', parameters: '()', returnType: 'Character', snippet: 'ToFormattedString()' },
        { name: 'IsEmpty', description: 'Checks if empty.', parameters: '()', returnType: 'Boolean', snippet: 'IsEmpty()' },
        { name: 'SetEmpty', description: 'Sets to empty value.', parameters: '()', returnType: 'None', snippet: 'SetEmpty()' }
    ]
};

// Aliases for common types
typeMethods['VarChar'] = typeMethods['Character'];
typeMethods['LongVarChar'] = typeMethods['Character'];

// Generic Collection methods
const collectionMethods: GxMethod[] = [
    { name: 'Add', description: 'Adds an item to the collection.', parameters: '(item)', returnType: 'None', snippet: 'Add(${1:item})' },
    { name: 'Remove', description: 'Removes an item from the collection by index.', parameters: '(index)', returnType: 'None', snippet: 'Remove(${1:index})' },
    { name: 'Clear', description: 'Removes all items from the collection.', parameters: '()', returnType: 'None', snippet: 'Clear()' },
    { name: 'Count', description: 'Returns the number of items in the collection.', parameters: '()', returnType: 'Numeric', snippet: 'Count()' },
    { name: 'Item', description: 'Returns an item from the collection by index.', parameters: '(index)', returnType: 'ItemType', snippet: 'Item(${1:index})' },
    { name: 'ToJson', description: 'Returns a JSON representation of the collection.', parameters: '()', returnType: 'Character', snippet: 'ToJson()' },
    { name: 'FromJson', description: 'Loads the collection from a JSON string.', parameters: '(string)', returnType: 'None', snippet: 'FromJson(${1:string})' }
];

typeMethods['Collection'] = collectionMethods;
