// @ts-nocheck
// tools/tutorial/generate-schema-validator.mjs が schema.json から生成する。編集しない(npm run gen:tutorial-validator)。
export const validate = validate10;
export default validate10;
const schema11 = {"type":"object","properties":{"version":{"const":1},"id":{"type":"string","minLength":1},"setup":{"type":"array","items":{"oneOf":[{"type":"object","properties":{"type":{"const":"create"},"resource":{"enum":["paths","parcels","zones","buildings"]},"bind":{"type":"string","minLength":1},"body":{"type":"object","additionalProperties":{"$ref":"#/definitions/value"}}},"required":["type","resource","bind","body"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"completeConstruction"},"target":{"type":"string","minLength":1}},"required":["type","target"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"reload"}},"required":["type"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"scenarioAction"},"scenario":{"enum":["scenario3","scenario4","trade","highway","mining","tourism","reclamation"]},"action":{"type":"string","minLength":1}},"required":["type","scenario","action"],"additionalProperties":false}]}},"steps":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string","minLength":1},"legacyStep":{"type":"integer","minimum":1},"phases":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string","minLength":1},"textKey":{"type":"string","minLength":1},"ui":{"type":"object","properties":{"policy":{"enum":["highlight","related","strict"]},"allow":{"type":"array","items":{"type":"string","minLength":1}},"highlight":{"type":"array","items":{"type":"string","minLength":1}}},"required":["policy","allow","highlight"],"additionalProperties":false},"world":{"type":"object","properties":{"mode":{"enum":["observe","blocked","path"]},"markers":{"type":"array","items":{"type":"array","items":{"type":"number"},"minItems":3,"maxItems":3}},"path":{"type":"object","properties":{"points":{"type":"array","items":{"type":"array","items":{"type":"number"},"minItems":3,"maxItems":3},"minItems":2,"maxItems":2},"radius":{"type":"number","exclusiveMinimum":0},"variant":{"$ref":"#/definitions/value"},"bind":{"type":"string","minLength":1}},"required":["points","radius","variant"],"additionalProperties":false},"camera":{"type":"object","properties":{"target":{"type":"array","items":{"type":"number"},"minItems":3,"maxItems":3},"distance":{"type":"number","exclusiveMinimum":0},"yaw":{"type":"number"},"pitch":{"type":"number","minimum":5,"maximum":89}},"required":["target","distance","yaw","pitch"],"additionalProperties":false}},"required":["mode"],"additionalProperties":false},"until":{"$ref":"#/definitions/condition"}},"required":["id","textKey","ui","world","until"],"additionalProperties":false},"minItems":1},"beforeCheck":{"type":"array","items":{"oneOf":[{"type":"object","properties":{"type":{"const":"create"},"resource":{"enum":["paths","parcels","zones","buildings"]},"bind":{"type":"string","minLength":1},"body":{"type":"object","additionalProperties":{"$ref":"#/definitions/value"}}},"required":["type","resource","bind","body"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"completeConstruction"},"target":{"type":"string","minLength":1}},"required":["type","target"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"reload"}},"required":["type"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"scenarioAction"},"scenario":{"enum":["scenario3","scenario4","trade","highway","mining","tourism","reclamation"]},"action":{"type":"string","minLength":1}},"required":["type","scenario","action"],"additionalProperties":false}]}},"complete":{"$ref":"#/definitions/condition"},"afterCheck":{"type":"array","items":{"oneOf":[{"type":"object","properties":{"type":{"const":"create"},"resource":{"enum":["paths","parcels","zones","buildings"]},"bind":{"type":"string","minLength":1},"body":{"type":"object","additionalProperties":{"$ref":"#/definitions/value"}}},"required":["type","resource","bind","body"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"completeConstruction"},"target":{"type":"string","minLength":1}},"required":["type","target"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"reload"}},"required":["type"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"scenarioAction"},"scenario":{"enum":["scenario3","scenario4","trade","highway","mining","tourism","reclamation"]},"action":{"type":"string","minLength":1}},"required":["type","scenario","action"],"additionalProperties":false}]}}},"required":["id","legacyStep","phases"],"additionalProperties":false}}},"required":["version","id","steps"],"additionalProperties":false,"$schema":"http://json-schema.org/draft-07/schema#","definitions":{"value":{"anyOf":[{"type":["null","boolean","number","string"]},{"type":"array","items":{"$ref":"#/definitions/value"}},{"type":"object","additionalProperties":{"$ref":"#/definitions/value"}}]},"match":{"type":"object","properties":{"field":{"type":"string","minLength":1},"equals":{"$ref":"#/definitions/value"},"notEquals":{"$ref":"#/definitions/value"},"oneOf":{"type":"array","items":{"$ref":"#/definitions/value"},"minItems":1},"greaterThan":{"type":"number"}},"required":["field"],"additionalProperties":false,"oneOf":[{"required":["equals"]},{"required":["notEquals"]},{"required":["oneOf"]},{"required":["greaterThan"]}]},"condition":{"oneOf":[{"type":"object","properties":{"type":{"const":"ack"}},"required":["type"],"additionalProperties":false},{"type":"object","properties":{"type":{"enum":["all","any"]},"conditions":{"type":"array","items":{"$ref":"#/definitions/condition"},"minItems":1}},"required":["type","conditions"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"window"},"window":{"type":"string","minLength":1},"target":{"type":"string","minLength":1}},"required":["type","window"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"pathVariant"},"value":{"$ref":"#/definitions/value"}},"required":["type","value"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"pathPoints"},"count":{"type":"integer","minimum":0},"phase":{"enum":["placing","editing","inactive"]}},"required":["type","count"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"entity"},"resource":{"enum":["paths","parcels","buildings","agents","zones","city","budgets","lines","tutorial"]},"where":{"type":"array","items":{"$ref":"#/definitions/match"}},"bind":{"type":"string","minLength":1},"target":{"type":"string","minLength":1}},"required":["type","resource","where"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"budgetRatio"},"category":{"type":"string","minLength":1},"min":{"type":"number","minimum":0,"maximum":1}},"required":["type","category","min"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"connected"},"from":{"type":"string","minLength":1},"to":{"type":"string","minLength":1},"terminal":{"type":"string","minLength":1},"where":{"type":"array","items":{"$ref":"#/definitions/match"}}},"required":["type","from","to","terminal","where"],"additionalProperties":false}]},"action":{"oneOf":[{"type":"object","properties":{"type":{"const":"create"},"resource":{"enum":["paths","parcels","zones","buildings"]},"bind":{"type":"string","minLength":1},"body":{"type":"object","additionalProperties":{"$ref":"#/definitions/value"}}},"required":["type","resource","bind","body"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"completeConstruction"},"target":{"type":"string","minLength":1}},"required":["type","target"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"reload"}},"required":["type"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"scenarioAction"},"scenario":{"enum":["scenario3","scenario4","trade","highway","mining","tourism","reclamation"]},"action":{"type":"string","minLength":1}},"required":["type","scenario","action"],"additionalProperties":false}]}}};
const func2 = (function ucs2length(str) {
    const len = str.length;
    let length = 0;
    let pos = 0;
    let value;
    while (pos < len) {
        length++;
        value = str.charCodeAt(pos++);
        if (value >= 0xd800 && value <= 0xdbff && pos < len) {
            // high surrogate, and there is a next character
            value = str.charCodeAt(pos);
            if ((value & 0xfc00) === 0xdc00)
                pos++; // low surrogate
        }
    }
    return length;
});
const schema12 = {"anyOf":[{"type":["null","boolean","number","string"]},{"type":"array","items":{"$ref":"#/definitions/value"}},{"type":"object","additionalProperties":{"$ref":"#/definitions/value"}}]};
const wrapper0 = {validate: validate11};

function validate11(data, {instancePath="", parentData, parentDataProperty, rootData=data}={}){
let vErrors = null;
let errors = 0;
const _errs0 = errors;
let valid0 = false;
const _errs1 = errors;
if((((data !== null) && (typeof data !== "boolean")) && (!(typeof data == "number"))) && (typeof data !== "string")){
const err0 = {instancePath,schemaPath:"#/anyOf/0/type",keyword:"type",params:{type: schema12.anyOf[0].type},message:"must be null,boolean,number,string"};
if(vErrors === null){
vErrors = [err0];
}
else {
vErrors.push(err0);
}
errors++;
}
var _valid0 = _errs1 === errors;
valid0 = valid0 || _valid0;
if(!valid0){
const _errs3 = errors;
if(Array.isArray(data)){
const len0 = data.length;
for(let i0=0; i0<len0; i0++){
if(!(wrapper0.validate(data[i0], {instancePath:instancePath+"/" + i0,parentData:data,parentDataProperty:i0,rootData}))){
vErrors = vErrors === null ? wrapper0.validate.errors : vErrors.concat(wrapper0.validate.errors);
errors = vErrors.length;
}
}
}
else {
const err1 = {instancePath,schemaPath:"#/anyOf/1/type",keyword:"type",params:{type: "array"},message:"must be array"};
if(vErrors === null){
vErrors = [err1];
}
else {
vErrors.push(err1);
}
errors++;
}
var _valid0 = _errs3 === errors;
valid0 = valid0 || _valid0;
if(!valid0){
const _errs6 = errors;
if(data && typeof data == "object" && !Array.isArray(data)){
for(const key0 in data){
if(!(wrapper0.validate(data[key0], {instancePath:instancePath+"/" + key0.replace(/~/g, "~0").replace(/\//g, "~1"),parentData:data,parentDataProperty:key0,rootData}))){
vErrors = vErrors === null ? wrapper0.validate.errors : vErrors.concat(wrapper0.validate.errors);
errors = vErrors.length;
}
}
}
else {
const err2 = {instancePath,schemaPath:"#/anyOf/2/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err2];
}
else {
vErrors.push(err2);
}
errors++;
}
var _valid0 = _errs6 === errors;
valid0 = valid0 || _valid0;
}
}
if(!valid0){
const err3 = {instancePath,schemaPath:"#/anyOf",keyword:"anyOf",params:{},message:"must match a schema in anyOf"};
if(vErrors === null){
vErrors = [err3];
}
else {
vErrors.push(err3);
}
errors++;
}
else {
errors = _errs0;
if(vErrors !== null){
if(_errs0){
vErrors.length = _errs0;
}
else {
vErrors = null;
}
}
}
validate11.errors = vErrors;
return errors === 0;
}

const schema13 = {"oneOf":[{"type":"object","properties":{"type":{"const":"ack"}},"required":["type"],"additionalProperties":false},{"type":"object","properties":{"type":{"enum":["all","any"]},"conditions":{"type":"array","items":{"$ref":"#/definitions/condition"},"minItems":1}},"required":["type","conditions"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"window"},"window":{"type":"string","minLength":1},"target":{"type":"string","minLength":1}},"required":["type","window"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"pathVariant"},"value":{"$ref":"#/definitions/value"}},"required":["type","value"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"pathPoints"},"count":{"type":"integer","minimum":0},"phase":{"enum":["placing","editing","inactive"]}},"required":["type","count"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"entity"},"resource":{"enum":["paths","parcels","buildings","agents","zones","city","budgets","lines","tutorial"]},"where":{"type":"array","items":{"$ref":"#/definitions/match"}},"bind":{"type":"string","minLength":1},"target":{"type":"string","minLength":1}},"required":["type","resource","where"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"budgetRatio"},"category":{"type":"string","minLength":1},"min":{"type":"number","minimum":0,"maximum":1}},"required":["type","category","min"],"additionalProperties":false},{"type":"object","properties":{"type":{"const":"connected"},"from":{"type":"string","minLength":1},"to":{"type":"string","minLength":1},"terminal":{"type":"string","minLength":1},"where":{"type":"array","items":{"$ref":"#/definitions/match"}}},"required":["type","from","to","terminal","where"],"additionalProperties":false}]};
const wrapper2 = {validate: validate14};
const schema14 = {"type":"object","properties":{"field":{"type":"string","minLength":1},"equals":{"$ref":"#/definitions/value"},"notEquals":{"$ref":"#/definitions/value"},"oneOf":{"type":"array","items":{"$ref":"#/definitions/value"},"minItems":1},"greaterThan":{"type":"number"}},"required":["field"],"additionalProperties":false,"oneOf":[{"required":["equals"]},{"required":["notEquals"]},{"required":["oneOf"]},{"required":["greaterThan"]}]};

function validate16(data, {instancePath="", parentData, parentDataProperty, rootData=data}={}){
let vErrors = null;
let errors = 0;
const _errs1 = errors;
let valid0 = false;
let passing0 = null;
const _errs2 = errors;
if(data && typeof data == "object" && !Array.isArray(data)){
if(data.equals === undefined){
const err0 = {instancePath,schemaPath:"#/oneOf/0/required",keyword:"required",params:{missingProperty: "equals"},message:"must have required property '"+"equals"+"'"};
if(vErrors === null){
vErrors = [err0];
}
else {
vErrors.push(err0);
}
errors++;
}
}
var _valid0 = _errs2 === errors;
if(_valid0){
valid0 = true;
passing0 = 0;
}
const _errs3 = errors;
if(data && typeof data == "object" && !Array.isArray(data)){
if(data.notEquals === undefined){
const err1 = {instancePath,schemaPath:"#/oneOf/1/required",keyword:"required",params:{missingProperty: "notEquals"},message:"must have required property '"+"notEquals"+"'"};
if(vErrors === null){
vErrors = [err1];
}
else {
vErrors.push(err1);
}
errors++;
}
}
var _valid0 = _errs3 === errors;
if(_valid0 && valid0){
valid0 = false;
passing0 = [passing0, 1];
}
else {
if(_valid0){
valid0 = true;
passing0 = 1;
}
const _errs4 = errors;
if(data && typeof data == "object" && !Array.isArray(data)){
if(data.oneOf === undefined){
const err2 = {instancePath,schemaPath:"#/oneOf/2/required",keyword:"required",params:{missingProperty: "oneOf"},message:"must have required property '"+"oneOf"+"'"};
if(vErrors === null){
vErrors = [err2];
}
else {
vErrors.push(err2);
}
errors++;
}
}
var _valid0 = _errs4 === errors;
if(_valid0 && valid0){
valid0 = false;
passing0 = [passing0, 2];
}
else {
if(_valid0){
valid0 = true;
passing0 = 2;
}
const _errs5 = errors;
if(data && typeof data == "object" && !Array.isArray(data)){
if(data.greaterThan === undefined){
const err3 = {instancePath,schemaPath:"#/oneOf/3/required",keyword:"required",params:{missingProperty: "greaterThan"},message:"must have required property '"+"greaterThan"+"'"};
if(vErrors === null){
vErrors = [err3];
}
else {
vErrors.push(err3);
}
errors++;
}
}
var _valid0 = _errs5 === errors;
if(_valid0 && valid0){
valid0 = false;
passing0 = [passing0, 3];
}
else {
if(_valid0){
valid0 = true;
passing0 = 3;
}
}
}
}
if(!valid0){
const err4 = {instancePath,schemaPath:"#/oneOf",keyword:"oneOf",params:{passingSchemas: passing0},message:"must match exactly one schema in oneOf"};
if(vErrors === null){
vErrors = [err4];
}
else {
vErrors.push(err4);
}
errors++;
}
else {
errors = _errs1;
if(vErrors !== null){
if(_errs1){
vErrors.length = _errs1;
}
else {
vErrors = null;
}
}
}
if(data && typeof data == "object" && !Array.isArray(data)){
if(data.field === undefined){
const err5 = {instancePath,schemaPath:"#/required",keyword:"required",params:{missingProperty: "field"},message:"must have required property '"+"field"+"'"};
if(vErrors === null){
vErrors = [err5];
}
else {
vErrors.push(err5);
}
errors++;
}
for(const key0 in data){
if(!(((((key0 === "field") || (key0 === "equals")) || (key0 === "notEquals")) || (key0 === "oneOf")) || (key0 === "greaterThan"))){
const err6 = {instancePath,schemaPath:"#/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key0},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err6];
}
else {
vErrors.push(err6);
}
errors++;
}
}
if(data.field !== undefined){
let data0 = data.field;
if(typeof data0 === "string"){
if(func2(data0) < 1){
const err7 = {instancePath:instancePath+"/field",schemaPath:"#/properties/field/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err7];
}
else {
vErrors.push(err7);
}
errors++;
}
}
else {
const err8 = {instancePath:instancePath+"/field",schemaPath:"#/properties/field/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err8];
}
else {
vErrors.push(err8);
}
errors++;
}
}
if(data.equals !== undefined){
if(!(validate11(data.equals, {instancePath:instancePath+"/equals",parentData:data,parentDataProperty:"equals",rootData}))){
vErrors = vErrors === null ? validate11.errors : vErrors.concat(validate11.errors);
errors = vErrors.length;
}
}
if(data.notEquals !== undefined){
if(!(validate11(data.notEquals, {instancePath:instancePath+"/notEquals",parentData:data,parentDataProperty:"notEquals",rootData}))){
vErrors = vErrors === null ? validate11.errors : vErrors.concat(validate11.errors);
errors = vErrors.length;
}
}
if(data.oneOf !== undefined){
let data3 = data.oneOf;
if(Array.isArray(data3)){
if(data3.length < 1){
const err9 = {instancePath:instancePath+"/oneOf",schemaPath:"#/properties/oneOf/minItems",keyword:"minItems",params:{limit: 1},message:"must NOT have fewer than 1 items"};
if(vErrors === null){
vErrors = [err9];
}
else {
vErrors.push(err9);
}
errors++;
}
const len0 = data3.length;
for(let i0=0; i0<len0; i0++){
if(!(validate11(data3[i0], {instancePath:instancePath+"/oneOf/" + i0,parentData:data3,parentDataProperty:i0,rootData}))){
vErrors = vErrors === null ? validate11.errors : vErrors.concat(validate11.errors);
errors = vErrors.length;
}
}
}
else {
const err10 = {instancePath:instancePath+"/oneOf",schemaPath:"#/properties/oneOf/type",keyword:"type",params:{type: "array"},message:"must be array"};
if(vErrors === null){
vErrors = [err10];
}
else {
vErrors.push(err10);
}
errors++;
}
}
if(data.greaterThan !== undefined){
if(!(typeof data.greaterThan == "number")){
const err11 = {instancePath:instancePath+"/greaterThan",schemaPath:"#/properties/greaterThan/type",keyword:"type",params:{type: "number"},message:"must be number"};
if(vErrors === null){
vErrors = [err11];
}
else {
vErrors.push(err11);
}
errors++;
}
}
}
else {
const err12 = {instancePath,schemaPath:"#/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err12];
}
else {
vErrors.push(err12);
}
errors++;
}
validate16.errors = vErrors;
return errors === 0;
}


function validate14(data, {instancePath="", parentData, parentDataProperty, rootData=data}={}){
let vErrors = null;
let errors = 0;
const _errs0 = errors;
let valid0 = false;
let passing0 = null;
const _errs1 = errors;
if(data && typeof data == "object" && !Array.isArray(data)){
if(data.type === undefined){
const err0 = {instancePath,schemaPath:"#/oneOf/0/required",keyword:"required",params:{missingProperty: "type"},message:"must have required property '"+"type"+"'"};
if(vErrors === null){
vErrors = [err0];
}
else {
vErrors.push(err0);
}
errors++;
}
for(const key0 in data){
if(!(key0 === "type")){
const err1 = {instancePath,schemaPath:"#/oneOf/0/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key0},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err1];
}
else {
vErrors.push(err1);
}
errors++;
}
}
if(data.type !== undefined){
if("ack" !== data.type){
const err2 = {instancePath:instancePath+"/type",schemaPath:"#/oneOf/0/properties/type/const",keyword:"const",params:{allowedValue: "ack"},message:"must be equal to constant"};
if(vErrors === null){
vErrors = [err2];
}
else {
vErrors.push(err2);
}
errors++;
}
}
}
else {
const err3 = {instancePath,schemaPath:"#/oneOf/0/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err3];
}
else {
vErrors.push(err3);
}
errors++;
}
var _valid0 = _errs1 === errors;
if(_valid0){
valid0 = true;
passing0 = 0;
}
const _errs5 = errors;
if(data && typeof data == "object" && !Array.isArray(data)){
if(data.type === undefined){
const err4 = {instancePath,schemaPath:"#/oneOf/1/required",keyword:"required",params:{missingProperty: "type"},message:"must have required property '"+"type"+"'"};
if(vErrors === null){
vErrors = [err4];
}
else {
vErrors.push(err4);
}
errors++;
}
if(data.conditions === undefined){
const err5 = {instancePath,schemaPath:"#/oneOf/1/required",keyword:"required",params:{missingProperty: "conditions"},message:"must have required property '"+"conditions"+"'"};
if(vErrors === null){
vErrors = [err5];
}
else {
vErrors.push(err5);
}
errors++;
}
for(const key1 in data){
if(!((key1 === "type") || (key1 === "conditions"))){
const err6 = {instancePath,schemaPath:"#/oneOf/1/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key1},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err6];
}
else {
vErrors.push(err6);
}
errors++;
}
}
if(data.type !== undefined){
let data1 = data.type;
if(!((data1 === "all") || (data1 === "any"))){
const err7 = {instancePath:instancePath+"/type",schemaPath:"#/oneOf/1/properties/type/enum",keyword:"enum",params:{allowedValues: schema13.oneOf[1].properties.type.enum},message:"must be equal to one of the allowed values"};
if(vErrors === null){
vErrors = [err7];
}
else {
vErrors.push(err7);
}
errors++;
}
}
if(data.conditions !== undefined){
let data2 = data.conditions;
if(Array.isArray(data2)){
if(data2.length < 1){
const err8 = {instancePath:instancePath+"/conditions",schemaPath:"#/oneOf/1/properties/conditions/minItems",keyword:"minItems",params:{limit: 1},message:"must NOT have fewer than 1 items"};
if(vErrors === null){
vErrors = [err8];
}
else {
vErrors.push(err8);
}
errors++;
}
const len0 = data2.length;
for(let i0=0; i0<len0; i0++){
if(!(wrapper2.validate(data2[i0], {instancePath:instancePath+"/conditions/" + i0,parentData:data2,parentDataProperty:i0,rootData}))){
vErrors = vErrors === null ? wrapper2.validate.errors : vErrors.concat(wrapper2.validate.errors);
errors = vErrors.length;
}
}
}
else {
const err9 = {instancePath:instancePath+"/conditions",schemaPath:"#/oneOf/1/properties/conditions/type",keyword:"type",params:{type: "array"},message:"must be array"};
if(vErrors === null){
vErrors = [err9];
}
else {
vErrors.push(err9);
}
errors++;
}
}
}
else {
const err10 = {instancePath,schemaPath:"#/oneOf/1/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err10];
}
else {
vErrors.push(err10);
}
errors++;
}
var _valid0 = _errs5 === errors;
if(_valid0 && valid0){
valid0 = false;
passing0 = [passing0, 1];
}
else {
if(_valid0){
valid0 = true;
passing0 = 1;
}
const _errs12 = errors;
if(data && typeof data == "object" && !Array.isArray(data)){
if(data.type === undefined){
const err11 = {instancePath,schemaPath:"#/oneOf/2/required",keyword:"required",params:{missingProperty: "type"},message:"must have required property '"+"type"+"'"};
if(vErrors === null){
vErrors = [err11];
}
else {
vErrors.push(err11);
}
errors++;
}
if(data.window === undefined){
const err12 = {instancePath,schemaPath:"#/oneOf/2/required",keyword:"required",params:{missingProperty: "window"},message:"must have required property '"+"window"+"'"};
if(vErrors === null){
vErrors = [err12];
}
else {
vErrors.push(err12);
}
errors++;
}
for(const key2 in data){
if(!(((key2 === "type") || (key2 === "window")) || (key2 === "target"))){
const err13 = {instancePath,schemaPath:"#/oneOf/2/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key2},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err13];
}
else {
vErrors.push(err13);
}
errors++;
}
}
if(data.type !== undefined){
if("window" !== data.type){
const err14 = {instancePath:instancePath+"/type",schemaPath:"#/oneOf/2/properties/type/const",keyword:"const",params:{allowedValue: "window"},message:"must be equal to constant"};
if(vErrors === null){
vErrors = [err14];
}
else {
vErrors.push(err14);
}
errors++;
}
}
if(data.window !== undefined){
let data5 = data.window;
if(typeof data5 === "string"){
if(func2(data5) < 1){
const err15 = {instancePath:instancePath+"/window",schemaPath:"#/oneOf/2/properties/window/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err15];
}
else {
vErrors.push(err15);
}
errors++;
}
}
else {
const err16 = {instancePath:instancePath+"/window",schemaPath:"#/oneOf/2/properties/window/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err16];
}
else {
vErrors.push(err16);
}
errors++;
}
}
if(data.target !== undefined){
let data6 = data.target;
if(typeof data6 === "string"){
if(func2(data6) < 1){
const err17 = {instancePath:instancePath+"/target",schemaPath:"#/oneOf/2/properties/target/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err17];
}
else {
vErrors.push(err17);
}
errors++;
}
}
else {
const err18 = {instancePath:instancePath+"/target",schemaPath:"#/oneOf/2/properties/target/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err18];
}
else {
vErrors.push(err18);
}
errors++;
}
}
}
else {
const err19 = {instancePath,schemaPath:"#/oneOf/2/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err19];
}
else {
vErrors.push(err19);
}
errors++;
}
var _valid0 = _errs12 === errors;
if(_valid0 && valid0){
valid0 = false;
passing0 = [passing0, 2];
}
else {
if(_valid0){
valid0 = true;
passing0 = 2;
}
const _errs20 = errors;
if(data && typeof data == "object" && !Array.isArray(data)){
if(data.type === undefined){
const err20 = {instancePath,schemaPath:"#/oneOf/3/required",keyword:"required",params:{missingProperty: "type"},message:"must have required property '"+"type"+"'"};
if(vErrors === null){
vErrors = [err20];
}
else {
vErrors.push(err20);
}
errors++;
}
if(data.value === undefined){
const err21 = {instancePath,schemaPath:"#/oneOf/3/required",keyword:"required",params:{missingProperty: "value"},message:"must have required property '"+"value"+"'"};
if(vErrors === null){
vErrors = [err21];
}
else {
vErrors.push(err21);
}
errors++;
}
for(const key3 in data){
if(!((key3 === "type") || (key3 === "value"))){
const err22 = {instancePath,schemaPath:"#/oneOf/3/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key3},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err22];
}
else {
vErrors.push(err22);
}
errors++;
}
}
if(data.type !== undefined){
if("pathVariant" !== data.type){
const err23 = {instancePath:instancePath+"/type",schemaPath:"#/oneOf/3/properties/type/const",keyword:"const",params:{allowedValue: "pathVariant"},message:"must be equal to constant"};
if(vErrors === null){
vErrors = [err23];
}
else {
vErrors.push(err23);
}
errors++;
}
}
if(data.value !== undefined){
if(!(validate11(data.value, {instancePath:instancePath+"/value",parentData:data,parentDataProperty:"value",rootData}))){
vErrors = vErrors === null ? validate11.errors : vErrors.concat(validate11.errors);
errors = vErrors.length;
}
}
}
else {
const err24 = {instancePath,schemaPath:"#/oneOf/3/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err24];
}
else {
vErrors.push(err24);
}
errors++;
}
var _valid0 = _errs20 === errors;
if(_valid0 && valid0){
valid0 = false;
passing0 = [passing0, 3];
}
else {
if(_valid0){
valid0 = true;
passing0 = 3;
}
const _errs25 = errors;
if(data && typeof data == "object" && !Array.isArray(data)){
if(data.type === undefined){
const err25 = {instancePath,schemaPath:"#/oneOf/4/required",keyword:"required",params:{missingProperty: "type"},message:"must have required property '"+"type"+"'"};
if(vErrors === null){
vErrors = [err25];
}
else {
vErrors.push(err25);
}
errors++;
}
if(data.count === undefined){
const err26 = {instancePath,schemaPath:"#/oneOf/4/required",keyword:"required",params:{missingProperty: "count"},message:"must have required property '"+"count"+"'"};
if(vErrors === null){
vErrors = [err26];
}
else {
vErrors.push(err26);
}
errors++;
}
for(const key4 in data){
if(!(((key4 === "type") || (key4 === "count")) || (key4 === "phase"))){
const err27 = {instancePath,schemaPath:"#/oneOf/4/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key4},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err27];
}
else {
vErrors.push(err27);
}
errors++;
}
}
if(data.type !== undefined){
if("pathPoints" !== data.type){
const err28 = {instancePath:instancePath+"/type",schemaPath:"#/oneOf/4/properties/type/const",keyword:"const",params:{allowedValue: "pathPoints"},message:"must be equal to constant"};
if(vErrors === null){
vErrors = [err28];
}
else {
vErrors.push(err28);
}
errors++;
}
}
if(data.count !== undefined){
let data10 = data.count;
if(!((typeof data10 == "number") && (!(data10 % 1) && !isNaN(data10)))){
const err29 = {instancePath:instancePath+"/count",schemaPath:"#/oneOf/4/properties/count/type",keyword:"type",params:{type: "integer"},message:"must be integer"};
if(vErrors === null){
vErrors = [err29];
}
else {
vErrors.push(err29);
}
errors++;
}
if(typeof data10 == "number"){
if(data10 < 0 || isNaN(data10)){
const err30 = {instancePath:instancePath+"/count",schemaPath:"#/oneOf/4/properties/count/minimum",keyword:"minimum",params:{comparison: ">=", limit: 0},message:"must be >= 0"};
if(vErrors === null){
vErrors = [err30];
}
else {
vErrors.push(err30);
}
errors++;
}
}
}
if(data.phase !== undefined){
let data11 = data.phase;
if(!(((data11 === "placing") || (data11 === "editing")) || (data11 === "inactive"))){
const err31 = {instancePath:instancePath+"/phase",schemaPath:"#/oneOf/4/properties/phase/enum",keyword:"enum",params:{allowedValues: schema13.oneOf[4].properties.phase.enum},message:"must be equal to one of the allowed values"};
if(vErrors === null){
vErrors = [err31];
}
else {
vErrors.push(err31);
}
errors++;
}
}
}
else {
const err32 = {instancePath,schemaPath:"#/oneOf/4/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err32];
}
else {
vErrors.push(err32);
}
errors++;
}
var _valid0 = _errs25 === errors;
if(_valid0 && valid0){
valid0 = false;
passing0 = [passing0, 4];
}
else {
if(_valid0){
valid0 = true;
passing0 = 4;
}
const _errs32 = errors;
if(data && typeof data == "object" && !Array.isArray(data)){
if(data.type === undefined){
const err33 = {instancePath,schemaPath:"#/oneOf/5/required",keyword:"required",params:{missingProperty: "type"},message:"must have required property '"+"type"+"'"};
if(vErrors === null){
vErrors = [err33];
}
else {
vErrors.push(err33);
}
errors++;
}
if(data.resource === undefined){
const err34 = {instancePath,schemaPath:"#/oneOf/5/required",keyword:"required",params:{missingProperty: "resource"},message:"must have required property '"+"resource"+"'"};
if(vErrors === null){
vErrors = [err34];
}
else {
vErrors.push(err34);
}
errors++;
}
if(data.where === undefined){
const err35 = {instancePath,schemaPath:"#/oneOf/5/required",keyword:"required",params:{missingProperty: "where"},message:"must have required property '"+"where"+"'"};
if(vErrors === null){
vErrors = [err35];
}
else {
vErrors.push(err35);
}
errors++;
}
for(const key5 in data){
if(!(((((key5 === "type") || (key5 === "resource")) || (key5 === "where")) || (key5 === "bind")) || (key5 === "target"))){
const err36 = {instancePath,schemaPath:"#/oneOf/5/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key5},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err36];
}
else {
vErrors.push(err36);
}
errors++;
}
}
if(data.type !== undefined){
if("entity" !== data.type){
const err37 = {instancePath:instancePath+"/type",schemaPath:"#/oneOf/5/properties/type/const",keyword:"const",params:{allowedValue: "entity"},message:"must be equal to constant"};
if(vErrors === null){
vErrors = [err37];
}
else {
vErrors.push(err37);
}
errors++;
}
}
if(data.resource !== undefined){
let data13 = data.resource;
if(!(((((((((data13 === "paths") || (data13 === "parcels")) || (data13 === "buildings")) || (data13 === "agents")) || (data13 === "zones")) || (data13 === "city")) || (data13 === "budgets")) || (data13 === "lines")) || (data13 === "tutorial"))){
const err38 = {instancePath:instancePath+"/resource",schemaPath:"#/oneOf/5/properties/resource/enum",keyword:"enum",params:{allowedValues: schema13.oneOf[5].properties.resource.enum},message:"must be equal to one of the allowed values"};
if(vErrors === null){
vErrors = [err38];
}
else {
vErrors.push(err38);
}
errors++;
}
}
if(data.where !== undefined){
let data14 = data.where;
if(Array.isArray(data14)){
const len1 = data14.length;
for(let i1=0; i1<len1; i1++){
if(!(validate16(data14[i1], {instancePath:instancePath+"/where/" + i1,parentData:data14,parentDataProperty:i1,rootData}))){
vErrors = vErrors === null ? validate16.errors : vErrors.concat(validate16.errors);
errors = vErrors.length;
}
}
}
else {
const err39 = {instancePath:instancePath+"/where",schemaPath:"#/oneOf/5/properties/where/type",keyword:"type",params:{type: "array"},message:"must be array"};
if(vErrors === null){
vErrors = [err39];
}
else {
vErrors.push(err39);
}
errors++;
}
}
if(data.bind !== undefined){
let data16 = data.bind;
if(typeof data16 === "string"){
if(func2(data16) < 1){
const err40 = {instancePath:instancePath+"/bind",schemaPath:"#/oneOf/5/properties/bind/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err40];
}
else {
vErrors.push(err40);
}
errors++;
}
}
else {
const err41 = {instancePath:instancePath+"/bind",schemaPath:"#/oneOf/5/properties/bind/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err41];
}
else {
vErrors.push(err41);
}
errors++;
}
}
if(data.target !== undefined){
let data17 = data.target;
if(typeof data17 === "string"){
if(func2(data17) < 1){
const err42 = {instancePath:instancePath+"/target",schemaPath:"#/oneOf/5/properties/target/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err42];
}
else {
vErrors.push(err42);
}
errors++;
}
}
else {
const err43 = {instancePath:instancePath+"/target",schemaPath:"#/oneOf/5/properties/target/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err43];
}
else {
vErrors.push(err43);
}
errors++;
}
}
}
else {
const err44 = {instancePath,schemaPath:"#/oneOf/5/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err44];
}
else {
vErrors.push(err44);
}
errors++;
}
var _valid0 = _errs32 === errors;
if(_valid0 && valid0){
valid0 = false;
passing0 = [passing0, 5];
}
else {
if(_valid0){
valid0 = true;
passing0 = 5;
}
const _errs44 = errors;
if(data && typeof data == "object" && !Array.isArray(data)){
if(data.type === undefined){
const err45 = {instancePath,schemaPath:"#/oneOf/6/required",keyword:"required",params:{missingProperty: "type"},message:"must have required property '"+"type"+"'"};
if(vErrors === null){
vErrors = [err45];
}
else {
vErrors.push(err45);
}
errors++;
}
if(data.category === undefined){
const err46 = {instancePath,schemaPath:"#/oneOf/6/required",keyword:"required",params:{missingProperty: "category"},message:"must have required property '"+"category"+"'"};
if(vErrors === null){
vErrors = [err46];
}
else {
vErrors.push(err46);
}
errors++;
}
if(data.min === undefined){
const err47 = {instancePath,schemaPath:"#/oneOf/6/required",keyword:"required",params:{missingProperty: "min"},message:"must have required property '"+"min"+"'"};
if(vErrors === null){
vErrors = [err47];
}
else {
vErrors.push(err47);
}
errors++;
}
for(const key6 in data){
if(!(((key6 === "type") || (key6 === "category")) || (key6 === "min"))){
const err48 = {instancePath,schemaPath:"#/oneOf/6/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key6},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err48];
}
else {
vErrors.push(err48);
}
errors++;
}
}
if(data.type !== undefined){
if("budgetRatio" !== data.type){
const err49 = {instancePath:instancePath+"/type",schemaPath:"#/oneOf/6/properties/type/const",keyword:"const",params:{allowedValue: "budgetRatio"},message:"must be equal to constant"};
if(vErrors === null){
vErrors = [err49];
}
else {
vErrors.push(err49);
}
errors++;
}
}
if(data.category !== undefined){
let data19 = data.category;
if(typeof data19 === "string"){
if(func2(data19) < 1){
const err50 = {instancePath:instancePath+"/category",schemaPath:"#/oneOf/6/properties/category/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err50];
}
else {
vErrors.push(err50);
}
errors++;
}
}
else {
const err51 = {instancePath:instancePath+"/category",schemaPath:"#/oneOf/6/properties/category/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err51];
}
else {
vErrors.push(err51);
}
errors++;
}
}
if(data.min !== undefined){
let data20 = data.min;
if(typeof data20 == "number"){
if(data20 > 1 || isNaN(data20)){
const err52 = {instancePath:instancePath+"/min",schemaPath:"#/oneOf/6/properties/min/maximum",keyword:"maximum",params:{comparison: "<=", limit: 1},message:"must be <= 1"};
if(vErrors === null){
vErrors = [err52];
}
else {
vErrors.push(err52);
}
errors++;
}
if(data20 < 0 || isNaN(data20)){
const err53 = {instancePath:instancePath+"/min",schemaPath:"#/oneOf/6/properties/min/minimum",keyword:"minimum",params:{comparison: ">=", limit: 0},message:"must be >= 0"};
if(vErrors === null){
vErrors = [err53];
}
else {
vErrors.push(err53);
}
errors++;
}
}
else {
const err54 = {instancePath:instancePath+"/min",schemaPath:"#/oneOf/6/properties/min/type",keyword:"type",params:{type: "number"},message:"must be number"};
if(vErrors === null){
vErrors = [err54];
}
else {
vErrors.push(err54);
}
errors++;
}
}
}
else {
const err55 = {instancePath,schemaPath:"#/oneOf/6/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err55];
}
else {
vErrors.push(err55);
}
errors++;
}
var _valid0 = _errs44 === errors;
if(_valid0 && valid0){
valid0 = false;
passing0 = [passing0, 6];
}
else {
if(_valid0){
valid0 = true;
passing0 = 6;
}
const _errs52 = errors;
if(data && typeof data == "object" && !Array.isArray(data)){
if(data.type === undefined){
const err56 = {instancePath,schemaPath:"#/oneOf/7/required",keyword:"required",params:{missingProperty: "type"},message:"must have required property '"+"type"+"'"};
if(vErrors === null){
vErrors = [err56];
}
else {
vErrors.push(err56);
}
errors++;
}
if(data.from === undefined){
const err57 = {instancePath,schemaPath:"#/oneOf/7/required",keyword:"required",params:{missingProperty: "from"},message:"must have required property '"+"from"+"'"};
if(vErrors === null){
vErrors = [err57];
}
else {
vErrors.push(err57);
}
errors++;
}
if(data.to === undefined){
const err58 = {instancePath,schemaPath:"#/oneOf/7/required",keyword:"required",params:{missingProperty: "to"},message:"must have required property '"+"to"+"'"};
if(vErrors === null){
vErrors = [err58];
}
else {
vErrors.push(err58);
}
errors++;
}
if(data.terminal === undefined){
const err59 = {instancePath,schemaPath:"#/oneOf/7/required",keyword:"required",params:{missingProperty: "terminal"},message:"must have required property '"+"terminal"+"'"};
if(vErrors === null){
vErrors = [err59];
}
else {
vErrors.push(err59);
}
errors++;
}
if(data.where === undefined){
const err60 = {instancePath,schemaPath:"#/oneOf/7/required",keyword:"required",params:{missingProperty: "where"},message:"must have required property '"+"where"+"'"};
if(vErrors === null){
vErrors = [err60];
}
else {
vErrors.push(err60);
}
errors++;
}
for(const key7 in data){
if(!(((((key7 === "type") || (key7 === "from")) || (key7 === "to")) || (key7 === "terminal")) || (key7 === "where"))){
const err61 = {instancePath,schemaPath:"#/oneOf/7/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key7},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err61];
}
else {
vErrors.push(err61);
}
errors++;
}
}
if(data.type !== undefined){
if("connected" !== data.type){
const err62 = {instancePath:instancePath+"/type",schemaPath:"#/oneOf/7/properties/type/const",keyword:"const",params:{allowedValue: "connected"},message:"must be equal to constant"};
if(vErrors === null){
vErrors = [err62];
}
else {
vErrors.push(err62);
}
errors++;
}
}
if(data.from !== undefined){
let data22 = data.from;
if(typeof data22 === "string"){
if(func2(data22) < 1){
const err63 = {instancePath:instancePath+"/from",schemaPath:"#/oneOf/7/properties/from/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err63];
}
else {
vErrors.push(err63);
}
errors++;
}
}
else {
const err64 = {instancePath:instancePath+"/from",schemaPath:"#/oneOf/7/properties/from/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err64];
}
else {
vErrors.push(err64);
}
errors++;
}
}
if(data.to !== undefined){
let data23 = data.to;
if(typeof data23 === "string"){
if(func2(data23) < 1){
const err65 = {instancePath:instancePath+"/to",schemaPath:"#/oneOf/7/properties/to/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err65];
}
else {
vErrors.push(err65);
}
errors++;
}
}
else {
const err66 = {instancePath:instancePath+"/to",schemaPath:"#/oneOf/7/properties/to/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err66];
}
else {
vErrors.push(err66);
}
errors++;
}
}
if(data.terminal !== undefined){
let data24 = data.terminal;
if(typeof data24 === "string"){
if(func2(data24) < 1){
const err67 = {instancePath:instancePath+"/terminal",schemaPath:"#/oneOf/7/properties/terminal/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err67];
}
else {
vErrors.push(err67);
}
errors++;
}
}
else {
const err68 = {instancePath:instancePath+"/terminal",schemaPath:"#/oneOf/7/properties/terminal/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err68];
}
else {
vErrors.push(err68);
}
errors++;
}
}
if(data.where !== undefined){
let data25 = data.where;
if(Array.isArray(data25)){
const len2 = data25.length;
for(let i2=0; i2<len2; i2++){
if(!(validate16(data25[i2], {instancePath:instancePath+"/where/" + i2,parentData:data25,parentDataProperty:i2,rootData}))){
vErrors = vErrors === null ? validate16.errors : vErrors.concat(validate16.errors);
errors = vErrors.length;
}
}
}
else {
const err69 = {instancePath:instancePath+"/where",schemaPath:"#/oneOf/7/properties/where/type",keyword:"type",params:{type: "array"},message:"must be array"};
if(vErrors === null){
vErrors = [err69];
}
else {
vErrors.push(err69);
}
errors++;
}
}
}
else {
const err70 = {instancePath,schemaPath:"#/oneOf/7/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err70];
}
else {
vErrors.push(err70);
}
errors++;
}
var _valid0 = _errs52 === errors;
if(_valid0 && valid0){
valid0 = false;
passing0 = [passing0, 7];
}
else {
if(_valid0){
valid0 = true;
passing0 = 7;
}
}
}
}
}
}
}
}
if(!valid0){
const err71 = {instancePath,schemaPath:"#/oneOf",keyword:"oneOf",params:{passingSchemas: passing0},message:"must match exactly one schema in oneOf"};
if(vErrors === null){
vErrors = [err71];
}
else {
vErrors.push(err71);
}
errors++;
}
else {
errors = _errs0;
if(vErrors !== null){
if(_errs0){
vErrors.length = _errs0;
}
else {
vErrors = null;
}
}
}
validate14.errors = vErrors;
return errors === 0;
}


function validate10(data, {instancePath="", parentData, parentDataProperty, rootData=data}={}){
let vErrors = null;
let errors = 0;
if(data && typeof data == "object" && !Array.isArray(data)){
if(data.version === undefined){
const err0 = {instancePath,schemaPath:"#/required",keyword:"required",params:{missingProperty: "version"},message:"must have required property '"+"version"+"'"};
if(vErrors === null){
vErrors = [err0];
}
else {
vErrors.push(err0);
}
errors++;
}
if(data.id === undefined){
const err1 = {instancePath,schemaPath:"#/required",keyword:"required",params:{missingProperty: "id"},message:"must have required property '"+"id"+"'"};
if(vErrors === null){
vErrors = [err1];
}
else {
vErrors.push(err1);
}
errors++;
}
if(data.steps === undefined){
const err2 = {instancePath,schemaPath:"#/required",keyword:"required",params:{missingProperty: "steps"},message:"must have required property '"+"steps"+"'"};
if(vErrors === null){
vErrors = [err2];
}
else {
vErrors.push(err2);
}
errors++;
}
for(const key0 in data){
if(!((((key0 === "version") || (key0 === "id")) || (key0 === "setup")) || (key0 === "steps"))){
const err3 = {instancePath,schemaPath:"#/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key0},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err3];
}
else {
vErrors.push(err3);
}
errors++;
}
}
if(data.version !== undefined){
if(1 !== data.version){
const err4 = {instancePath:instancePath+"/version",schemaPath:"#/properties/version/const",keyword:"const",params:{allowedValue: 1},message:"must be equal to constant"};
if(vErrors === null){
vErrors = [err4];
}
else {
vErrors.push(err4);
}
errors++;
}
}
if(data.id !== undefined){
let data1 = data.id;
if(typeof data1 === "string"){
if(func2(data1) < 1){
const err5 = {instancePath:instancePath+"/id",schemaPath:"#/properties/id/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err5];
}
else {
vErrors.push(err5);
}
errors++;
}
}
else {
const err6 = {instancePath:instancePath+"/id",schemaPath:"#/properties/id/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err6];
}
else {
vErrors.push(err6);
}
errors++;
}
}
if(data.setup !== undefined){
let data2 = data.setup;
if(Array.isArray(data2)){
const len0 = data2.length;
for(let i0=0; i0<len0; i0++){
let data3 = data2[i0];
const _errs8 = errors;
let valid3 = false;
let passing0 = null;
const _errs9 = errors;
if(data3 && typeof data3 == "object" && !Array.isArray(data3)){
if(data3.type === undefined){
const err7 = {instancePath:instancePath+"/setup/" + i0,schemaPath:"#/properties/setup/items/oneOf/0/required",keyword:"required",params:{missingProperty: "type"},message:"must have required property '"+"type"+"'"};
if(vErrors === null){
vErrors = [err7];
}
else {
vErrors.push(err7);
}
errors++;
}
if(data3.resource === undefined){
const err8 = {instancePath:instancePath+"/setup/" + i0,schemaPath:"#/properties/setup/items/oneOf/0/required",keyword:"required",params:{missingProperty: "resource"},message:"must have required property '"+"resource"+"'"};
if(vErrors === null){
vErrors = [err8];
}
else {
vErrors.push(err8);
}
errors++;
}
if(data3.bind === undefined){
const err9 = {instancePath:instancePath+"/setup/" + i0,schemaPath:"#/properties/setup/items/oneOf/0/required",keyword:"required",params:{missingProperty: "bind"},message:"must have required property '"+"bind"+"'"};
if(vErrors === null){
vErrors = [err9];
}
else {
vErrors.push(err9);
}
errors++;
}
if(data3.body === undefined){
const err10 = {instancePath:instancePath+"/setup/" + i0,schemaPath:"#/properties/setup/items/oneOf/0/required",keyword:"required",params:{missingProperty: "body"},message:"must have required property '"+"body"+"'"};
if(vErrors === null){
vErrors = [err10];
}
else {
vErrors.push(err10);
}
errors++;
}
for(const key1 in data3){
if(!((((key1 === "type") || (key1 === "resource")) || (key1 === "bind")) || (key1 === "body"))){
const err11 = {instancePath:instancePath+"/setup/" + i0,schemaPath:"#/properties/setup/items/oneOf/0/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key1},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err11];
}
else {
vErrors.push(err11);
}
errors++;
}
}
if(data3.type !== undefined){
if("create" !== data3.type){
const err12 = {instancePath:instancePath+"/setup/" + i0+"/type",schemaPath:"#/properties/setup/items/oneOf/0/properties/type/const",keyword:"const",params:{allowedValue: "create"},message:"must be equal to constant"};
if(vErrors === null){
vErrors = [err12];
}
else {
vErrors.push(err12);
}
errors++;
}
}
if(data3.resource !== undefined){
let data5 = data3.resource;
if(!((((data5 === "paths") || (data5 === "parcels")) || (data5 === "zones")) || (data5 === "buildings"))){
const err13 = {instancePath:instancePath+"/setup/" + i0+"/resource",schemaPath:"#/properties/setup/items/oneOf/0/properties/resource/enum",keyword:"enum",params:{allowedValues: schema11.properties.setup.items.oneOf[0].properties.resource.enum},message:"must be equal to one of the allowed values"};
if(vErrors === null){
vErrors = [err13];
}
else {
vErrors.push(err13);
}
errors++;
}
}
if(data3.bind !== undefined){
let data6 = data3.bind;
if(typeof data6 === "string"){
if(func2(data6) < 1){
const err14 = {instancePath:instancePath+"/setup/" + i0+"/bind",schemaPath:"#/properties/setup/items/oneOf/0/properties/bind/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err14];
}
else {
vErrors.push(err14);
}
errors++;
}
}
else {
const err15 = {instancePath:instancePath+"/setup/" + i0+"/bind",schemaPath:"#/properties/setup/items/oneOf/0/properties/bind/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err15];
}
else {
vErrors.push(err15);
}
errors++;
}
}
if(data3.body !== undefined){
let data7 = data3.body;
if(data7 && typeof data7 == "object" && !Array.isArray(data7)){
for(const key2 in data7){
if(!(validate11(data7[key2], {instancePath:instancePath+"/setup/" + i0+"/body/" + key2.replace(/~/g, "~0").replace(/\//g, "~1"),parentData:data7,parentDataProperty:key2,rootData}))){
vErrors = vErrors === null ? validate11.errors : vErrors.concat(validate11.errors);
errors = vErrors.length;
}
}
}
else {
const err16 = {instancePath:instancePath+"/setup/" + i0+"/body",schemaPath:"#/properties/setup/items/oneOf/0/properties/body/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err16];
}
else {
vErrors.push(err16);
}
errors++;
}
}
}
else {
const err17 = {instancePath:instancePath+"/setup/" + i0,schemaPath:"#/properties/setup/items/oneOf/0/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err17];
}
else {
vErrors.push(err17);
}
errors++;
}
var _valid0 = _errs9 === errors;
if(_valid0){
valid3 = true;
passing0 = 0;
}
const _errs20 = errors;
if(data3 && typeof data3 == "object" && !Array.isArray(data3)){
if(data3.type === undefined){
const err18 = {instancePath:instancePath+"/setup/" + i0,schemaPath:"#/properties/setup/items/oneOf/1/required",keyword:"required",params:{missingProperty: "type"},message:"must have required property '"+"type"+"'"};
if(vErrors === null){
vErrors = [err18];
}
else {
vErrors.push(err18);
}
errors++;
}
if(data3.target === undefined){
const err19 = {instancePath:instancePath+"/setup/" + i0,schemaPath:"#/properties/setup/items/oneOf/1/required",keyword:"required",params:{missingProperty: "target"},message:"must have required property '"+"target"+"'"};
if(vErrors === null){
vErrors = [err19];
}
else {
vErrors.push(err19);
}
errors++;
}
for(const key3 in data3){
if(!((key3 === "type") || (key3 === "target"))){
const err20 = {instancePath:instancePath+"/setup/" + i0,schemaPath:"#/properties/setup/items/oneOf/1/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key3},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err20];
}
else {
vErrors.push(err20);
}
errors++;
}
}
if(data3.type !== undefined){
if("completeConstruction" !== data3.type){
const err21 = {instancePath:instancePath+"/setup/" + i0+"/type",schemaPath:"#/properties/setup/items/oneOf/1/properties/type/const",keyword:"const",params:{allowedValue: "completeConstruction"},message:"must be equal to constant"};
if(vErrors === null){
vErrors = [err21];
}
else {
vErrors.push(err21);
}
errors++;
}
}
if(data3.target !== undefined){
let data10 = data3.target;
if(typeof data10 === "string"){
if(func2(data10) < 1){
const err22 = {instancePath:instancePath+"/setup/" + i0+"/target",schemaPath:"#/properties/setup/items/oneOf/1/properties/target/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err22];
}
else {
vErrors.push(err22);
}
errors++;
}
}
else {
const err23 = {instancePath:instancePath+"/setup/" + i0+"/target",schemaPath:"#/properties/setup/items/oneOf/1/properties/target/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err23];
}
else {
vErrors.push(err23);
}
errors++;
}
}
}
else {
const err24 = {instancePath:instancePath+"/setup/" + i0,schemaPath:"#/properties/setup/items/oneOf/1/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err24];
}
else {
vErrors.push(err24);
}
errors++;
}
var _valid0 = _errs20 === errors;
if(_valid0 && valid3){
valid3 = false;
passing0 = [passing0, 1];
}
else {
if(_valid0){
valid3 = true;
passing0 = 1;
}
const _errs26 = errors;
if(data3 && typeof data3 == "object" && !Array.isArray(data3)){
if(data3.type === undefined){
const err25 = {instancePath:instancePath+"/setup/" + i0,schemaPath:"#/properties/setup/items/oneOf/2/required",keyword:"required",params:{missingProperty: "type"},message:"must have required property '"+"type"+"'"};
if(vErrors === null){
vErrors = [err25];
}
else {
vErrors.push(err25);
}
errors++;
}
for(const key4 in data3){
if(!(key4 === "type")){
const err26 = {instancePath:instancePath+"/setup/" + i0,schemaPath:"#/properties/setup/items/oneOf/2/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key4},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err26];
}
else {
vErrors.push(err26);
}
errors++;
}
}
if(data3.type !== undefined){
if("reload" !== data3.type){
const err27 = {instancePath:instancePath+"/setup/" + i0+"/type",schemaPath:"#/properties/setup/items/oneOf/2/properties/type/const",keyword:"const",params:{allowedValue: "reload"},message:"must be equal to constant"};
if(vErrors === null){
vErrors = [err27];
}
else {
vErrors.push(err27);
}
errors++;
}
}
}
else {
const err28 = {instancePath:instancePath+"/setup/" + i0,schemaPath:"#/properties/setup/items/oneOf/2/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err28];
}
else {
vErrors.push(err28);
}
errors++;
}
var _valid0 = _errs26 === errors;
if(_valid0 && valid3){
valid3 = false;
passing0 = [passing0, 2];
}
else {
if(_valid0){
valid3 = true;
passing0 = 2;
}
const _errs30 = errors;
if(data3 && typeof data3 == "object" && !Array.isArray(data3)){
if(data3.type === undefined){
const err29 = {instancePath:instancePath+"/setup/" + i0,schemaPath:"#/properties/setup/items/oneOf/3/required",keyword:"required",params:{missingProperty: "type"},message:"must have required property '"+"type"+"'"};
if(vErrors === null){
vErrors = [err29];
}
else {
vErrors.push(err29);
}
errors++;
}
if(data3.scenario === undefined){
const err30 = {instancePath:instancePath+"/setup/" + i0,schemaPath:"#/properties/setup/items/oneOf/3/required",keyword:"required",params:{missingProperty: "scenario"},message:"must have required property '"+"scenario"+"'"};
if(vErrors === null){
vErrors = [err30];
}
else {
vErrors.push(err30);
}
errors++;
}
if(data3.action === undefined){
const err31 = {instancePath:instancePath+"/setup/" + i0,schemaPath:"#/properties/setup/items/oneOf/3/required",keyword:"required",params:{missingProperty: "action"},message:"must have required property '"+"action"+"'"};
if(vErrors === null){
vErrors = [err31];
}
else {
vErrors.push(err31);
}
errors++;
}
for(const key5 in data3){
if(!(((key5 === "type") || (key5 === "scenario")) || (key5 === "action"))){
const err32 = {instancePath:instancePath+"/setup/" + i0,schemaPath:"#/properties/setup/items/oneOf/3/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key5},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err32];
}
else {
vErrors.push(err32);
}
errors++;
}
}
if(data3.type !== undefined){
if("scenarioAction" !== data3.type){
const err33 = {instancePath:instancePath+"/setup/" + i0+"/type",schemaPath:"#/properties/setup/items/oneOf/3/properties/type/const",keyword:"const",params:{allowedValue: "scenarioAction"},message:"must be equal to constant"};
if(vErrors === null){
vErrors = [err33];
}
else {
vErrors.push(err33);
}
errors++;
}
}
if(data3.scenario !== undefined){
let data13 = data3.scenario;
if(!(((((((data13 === "scenario3") || (data13 === "scenario4")) || (data13 === "trade")) || (data13 === "highway")) || (data13 === "mining")) || (data13 === "tourism")) || (data13 === "reclamation"))){
const err34 = {instancePath:instancePath+"/setup/" + i0+"/scenario",schemaPath:"#/properties/setup/items/oneOf/3/properties/scenario/enum",keyword:"enum",params:{allowedValues: schema11.properties.setup.items.oneOf[3].properties.scenario.enum},message:"must be equal to one of the allowed values"};
if(vErrors === null){
vErrors = [err34];
}
else {
vErrors.push(err34);
}
errors++;
}
}
if(data3.action !== undefined){
let data14 = data3.action;
if(typeof data14 === "string"){
if(func2(data14) < 1){
const err35 = {instancePath:instancePath+"/setup/" + i0+"/action",schemaPath:"#/properties/setup/items/oneOf/3/properties/action/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err35];
}
else {
vErrors.push(err35);
}
errors++;
}
}
else {
const err36 = {instancePath:instancePath+"/setup/" + i0+"/action",schemaPath:"#/properties/setup/items/oneOf/3/properties/action/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err36];
}
else {
vErrors.push(err36);
}
errors++;
}
}
}
else {
const err37 = {instancePath:instancePath+"/setup/" + i0,schemaPath:"#/properties/setup/items/oneOf/3/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err37];
}
else {
vErrors.push(err37);
}
errors++;
}
var _valid0 = _errs30 === errors;
if(_valid0 && valid3){
valid3 = false;
passing0 = [passing0, 3];
}
else {
if(_valid0){
valid3 = true;
passing0 = 3;
}
}
}
}
if(!valid3){
const err38 = {instancePath:instancePath+"/setup/" + i0,schemaPath:"#/properties/setup/items/oneOf",keyword:"oneOf",params:{passingSchemas: passing0},message:"must match exactly one schema in oneOf"};
if(vErrors === null){
vErrors = [err38];
}
else {
vErrors.push(err38);
}
errors++;
}
else {
errors = _errs8;
if(vErrors !== null){
if(_errs8){
vErrors.length = _errs8;
}
else {
vErrors = null;
}
}
}
}
}
else {
const err39 = {instancePath:instancePath+"/setup",schemaPath:"#/properties/setup/type",keyword:"type",params:{type: "array"},message:"must be array"};
if(vErrors === null){
vErrors = [err39];
}
else {
vErrors.push(err39);
}
errors++;
}
}
if(data.steps !== undefined){
let data15 = data.steps;
if(Array.isArray(data15)){
const len1 = data15.length;
for(let i1=0; i1<len1; i1++){
let data16 = data15[i1];
if(data16 && typeof data16 == "object" && !Array.isArray(data16)){
if(data16.id === undefined){
const err40 = {instancePath:instancePath+"/steps/" + i1,schemaPath:"#/properties/steps/items/required",keyword:"required",params:{missingProperty: "id"},message:"must have required property '"+"id"+"'"};
if(vErrors === null){
vErrors = [err40];
}
else {
vErrors.push(err40);
}
errors++;
}
if(data16.legacyStep === undefined){
const err41 = {instancePath:instancePath+"/steps/" + i1,schemaPath:"#/properties/steps/items/required",keyword:"required",params:{missingProperty: "legacyStep"},message:"must have required property '"+"legacyStep"+"'"};
if(vErrors === null){
vErrors = [err41];
}
else {
vErrors.push(err41);
}
errors++;
}
if(data16.phases === undefined){
const err42 = {instancePath:instancePath+"/steps/" + i1,schemaPath:"#/properties/steps/items/required",keyword:"required",params:{missingProperty: "phases"},message:"must have required property '"+"phases"+"'"};
if(vErrors === null){
vErrors = [err42];
}
else {
vErrors.push(err42);
}
errors++;
}
for(const key6 in data16){
if(!((((((key6 === "id") || (key6 === "legacyStep")) || (key6 === "phases")) || (key6 === "beforeCheck")) || (key6 === "complete")) || (key6 === "afterCheck"))){
const err43 = {instancePath:instancePath+"/steps/" + i1,schemaPath:"#/properties/steps/items/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key6},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err43];
}
else {
vErrors.push(err43);
}
errors++;
}
}
if(data16.id !== undefined){
let data17 = data16.id;
if(typeof data17 === "string"){
if(func2(data17) < 1){
const err44 = {instancePath:instancePath+"/steps/" + i1+"/id",schemaPath:"#/properties/steps/items/properties/id/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err44];
}
else {
vErrors.push(err44);
}
errors++;
}
}
else {
const err45 = {instancePath:instancePath+"/steps/" + i1+"/id",schemaPath:"#/properties/steps/items/properties/id/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err45];
}
else {
vErrors.push(err45);
}
errors++;
}
}
if(data16.legacyStep !== undefined){
let data18 = data16.legacyStep;
if(!((typeof data18 == "number") && (!(data18 % 1) && !isNaN(data18)))){
const err46 = {instancePath:instancePath+"/steps/" + i1+"/legacyStep",schemaPath:"#/properties/steps/items/properties/legacyStep/type",keyword:"type",params:{type: "integer"},message:"must be integer"};
if(vErrors === null){
vErrors = [err46];
}
else {
vErrors.push(err46);
}
errors++;
}
if(typeof data18 == "number"){
if(data18 < 1 || isNaN(data18)){
const err47 = {instancePath:instancePath+"/steps/" + i1+"/legacyStep",schemaPath:"#/properties/steps/items/properties/legacyStep/minimum",keyword:"minimum",params:{comparison: ">=", limit: 1},message:"must be >= 1"};
if(vErrors === null){
vErrors = [err47];
}
else {
vErrors.push(err47);
}
errors++;
}
}
}
if(data16.phases !== undefined){
let data19 = data16.phases;
if(Array.isArray(data19)){
if(data19.length < 1){
const err48 = {instancePath:instancePath+"/steps/" + i1+"/phases",schemaPath:"#/properties/steps/items/properties/phases/minItems",keyword:"minItems",params:{limit: 1},message:"must NOT have fewer than 1 items"};
if(vErrors === null){
vErrors = [err48];
}
else {
vErrors.push(err48);
}
errors++;
}
const len2 = data19.length;
for(let i2=0; i2<len2; i2++){
let data20 = data19[i2];
if(data20 && typeof data20 == "object" && !Array.isArray(data20)){
if(data20.id === undefined){
const err49 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2,schemaPath:"#/properties/steps/items/properties/phases/items/required",keyword:"required",params:{missingProperty: "id"},message:"must have required property '"+"id"+"'"};
if(vErrors === null){
vErrors = [err49];
}
else {
vErrors.push(err49);
}
errors++;
}
if(data20.textKey === undefined){
const err50 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2,schemaPath:"#/properties/steps/items/properties/phases/items/required",keyword:"required",params:{missingProperty: "textKey"},message:"must have required property '"+"textKey"+"'"};
if(vErrors === null){
vErrors = [err50];
}
else {
vErrors.push(err50);
}
errors++;
}
if(data20.ui === undefined){
const err51 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2,schemaPath:"#/properties/steps/items/properties/phases/items/required",keyword:"required",params:{missingProperty: "ui"},message:"must have required property '"+"ui"+"'"};
if(vErrors === null){
vErrors = [err51];
}
else {
vErrors.push(err51);
}
errors++;
}
if(data20.world === undefined){
const err52 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2,schemaPath:"#/properties/steps/items/properties/phases/items/required",keyword:"required",params:{missingProperty: "world"},message:"must have required property '"+"world"+"'"};
if(vErrors === null){
vErrors = [err52];
}
else {
vErrors.push(err52);
}
errors++;
}
if(data20.until === undefined){
const err53 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2,schemaPath:"#/properties/steps/items/properties/phases/items/required",keyword:"required",params:{missingProperty: "until"},message:"must have required property '"+"until"+"'"};
if(vErrors === null){
vErrors = [err53];
}
else {
vErrors.push(err53);
}
errors++;
}
for(const key7 in data20){
if(!(((((key7 === "id") || (key7 === "textKey")) || (key7 === "ui")) || (key7 === "world")) || (key7 === "until"))){
const err54 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2,schemaPath:"#/properties/steps/items/properties/phases/items/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key7},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err54];
}
else {
vErrors.push(err54);
}
errors++;
}
}
if(data20.id !== undefined){
let data21 = data20.id;
if(typeof data21 === "string"){
if(func2(data21) < 1){
const err55 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/id",schemaPath:"#/properties/steps/items/properties/phases/items/properties/id/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err55];
}
else {
vErrors.push(err55);
}
errors++;
}
}
else {
const err56 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/id",schemaPath:"#/properties/steps/items/properties/phases/items/properties/id/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err56];
}
else {
vErrors.push(err56);
}
errors++;
}
}
if(data20.textKey !== undefined){
let data22 = data20.textKey;
if(typeof data22 === "string"){
if(func2(data22) < 1){
const err57 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/textKey",schemaPath:"#/properties/steps/items/properties/phases/items/properties/textKey/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err57];
}
else {
vErrors.push(err57);
}
errors++;
}
}
else {
const err58 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/textKey",schemaPath:"#/properties/steps/items/properties/phases/items/properties/textKey/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err58];
}
else {
vErrors.push(err58);
}
errors++;
}
}
if(data20.ui !== undefined){
let data23 = data20.ui;
if(data23 && typeof data23 == "object" && !Array.isArray(data23)){
if(data23.policy === undefined){
const err59 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/ui",schemaPath:"#/properties/steps/items/properties/phases/items/properties/ui/required",keyword:"required",params:{missingProperty: "policy"},message:"must have required property '"+"policy"+"'"};
if(vErrors === null){
vErrors = [err59];
}
else {
vErrors.push(err59);
}
errors++;
}
if(data23.allow === undefined){
const err60 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/ui",schemaPath:"#/properties/steps/items/properties/phases/items/properties/ui/required",keyword:"required",params:{missingProperty: "allow"},message:"must have required property '"+"allow"+"'"};
if(vErrors === null){
vErrors = [err60];
}
else {
vErrors.push(err60);
}
errors++;
}
if(data23.highlight === undefined){
const err61 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/ui",schemaPath:"#/properties/steps/items/properties/phases/items/properties/ui/required",keyword:"required",params:{missingProperty: "highlight"},message:"must have required property '"+"highlight"+"'"};
if(vErrors === null){
vErrors = [err61];
}
else {
vErrors.push(err61);
}
errors++;
}
for(const key8 in data23){
if(!(((key8 === "policy") || (key8 === "allow")) || (key8 === "highlight"))){
const err62 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/ui",schemaPath:"#/properties/steps/items/properties/phases/items/properties/ui/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key8},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err62];
}
else {
vErrors.push(err62);
}
errors++;
}
}
if(data23.policy !== undefined){
let data24 = data23.policy;
if(!(((data24 === "highlight") || (data24 === "related")) || (data24 === "strict"))){
const err63 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/ui/policy",schemaPath:"#/properties/steps/items/properties/phases/items/properties/ui/properties/policy/enum",keyword:"enum",params:{allowedValues: schema11.properties.steps.items.properties.phases.items.properties.ui.properties.policy.enum},message:"must be equal to one of the allowed values"};
if(vErrors === null){
vErrors = [err63];
}
else {
vErrors.push(err63);
}
errors++;
}
}
if(data23.allow !== undefined){
let data25 = data23.allow;
if(Array.isArray(data25)){
const len3 = data25.length;
for(let i3=0; i3<len3; i3++){
let data26 = data25[i3];
if(typeof data26 === "string"){
if(func2(data26) < 1){
const err64 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/ui/allow/" + i3,schemaPath:"#/properties/steps/items/properties/phases/items/properties/ui/properties/allow/items/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err64];
}
else {
vErrors.push(err64);
}
errors++;
}
}
else {
const err65 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/ui/allow/" + i3,schemaPath:"#/properties/steps/items/properties/phases/items/properties/ui/properties/allow/items/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err65];
}
else {
vErrors.push(err65);
}
errors++;
}
}
}
else {
const err66 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/ui/allow",schemaPath:"#/properties/steps/items/properties/phases/items/properties/ui/properties/allow/type",keyword:"type",params:{type: "array"},message:"must be array"};
if(vErrors === null){
vErrors = [err66];
}
else {
vErrors.push(err66);
}
errors++;
}
}
if(data23.highlight !== undefined){
let data27 = data23.highlight;
if(Array.isArray(data27)){
const len4 = data27.length;
for(let i4=0; i4<len4; i4++){
let data28 = data27[i4];
if(typeof data28 === "string"){
if(func2(data28) < 1){
const err67 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/ui/highlight/" + i4,schemaPath:"#/properties/steps/items/properties/phases/items/properties/ui/properties/highlight/items/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err67];
}
else {
vErrors.push(err67);
}
errors++;
}
}
else {
const err68 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/ui/highlight/" + i4,schemaPath:"#/properties/steps/items/properties/phases/items/properties/ui/properties/highlight/items/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err68];
}
else {
vErrors.push(err68);
}
errors++;
}
}
}
else {
const err69 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/ui/highlight",schemaPath:"#/properties/steps/items/properties/phases/items/properties/ui/properties/highlight/type",keyword:"type",params:{type: "array"},message:"must be array"};
if(vErrors === null){
vErrors = [err69];
}
else {
vErrors.push(err69);
}
errors++;
}
}
}
else {
const err70 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/ui",schemaPath:"#/properties/steps/items/properties/phases/items/properties/ui/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err70];
}
else {
vErrors.push(err70);
}
errors++;
}
}
if(data20.world !== undefined){
let data29 = data20.world;
if(data29 && typeof data29 == "object" && !Array.isArray(data29)){
if(data29.mode === undefined){
const err71 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/required",keyword:"required",params:{missingProperty: "mode"},message:"must have required property '"+"mode"+"'"};
if(vErrors === null){
vErrors = [err71];
}
else {
vErrors.push(err71);
}
errors++;
}
for(const key9 in data29){
if(!((((key9 === "mode") || (key9 === "markers")) || (key9 === "path")) || (key9 === "camera"))){
const err72 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key9},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err72];
}
else {
vErrors.push(err72);
}
errors++;
}
}
if(data29.mode !== undefined){
let data30 = data29.mode;
if(!(((data30 === "observe") || (data30 === "blocked")) || (data30 === "path"))){
const err73 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/mode",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/mode/enum",keyword:"enum",params:{allowedValues: schema11.properties.steps.items.properties.phases.items.properties.world.properties.mode.enum},message:"must be equal to one of the allowed values"};
if(vErrors === null){
vErrors = [err73];
}
else {
vErrors.push(err73);
}
errors++;
}
}
if(data29.markers !== undefined){
let data31 = data29.markers;
if(Array.isArray(data31)){
const len5 = data31.length;
for(let i5=0; i5<len5; i5++){
let data32 = data31[i5];
if(Array.isArray(data32)){
if(data32.length > 3){
const err74 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/markers/" + i5,schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/markers/items/maxItems",keyword:"maxItems",params:{limit: 3},message:"must NOT have more than 3 items"};
if(vErrors === null){
vErrors = [err74];
}
else {
vErrors.push(err74);
}
errors++;
}
if(data32.length < 3){
const err75 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/markers/" + i5,schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/markers/items/minItems",keyword:"minItems",params:{limit: 3},message:"must NOT have fewer than 3 items"};
if(vErrors === null){
vErrors = [err75];
}
else {
vErrors.push(err75);
}
errors++;
}
const len6 = data32.length;
for(let i6=0; i6<len6; i6++){
if(!(typeof data32[i6] == "number")){
const err76 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/markers/" + i5+"/" + i6,schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/markers/items/items/type",keyword:"type",params:{type: "number"},message:"must be number"};
if(vErrors === null){
vErrors = [err76];
}
else {
vErrors.push(err76);
}
errors++;
}
}
}
else {
const err77 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/markers/" + i5,schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/markers/items/type",keyword:"type",params:{type: "array"},message:"must be array"};
if(vErrors === null){
vErrors = [err77];
}
else {
vErrors.push(err77);
}
errors++;
}
}
}
else {
const err78 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/markers",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/markers/type",keyword:"type",params:{type: "array"},message:"must be array"};
if(vErrors === null){
vErrors = [err78];
}
else {
vErrors.push(err78);
}
errors++;
}
}
if(data29.path !== undefined){
let data34 = data29.path;
if(data34 && typeof data34 == "object" && !Array.isArray(data34)){
if(data34.points === undefined){
const err79 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/path",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/path/required",keyword:"required",params:{missingProperty: "points"},message:"must have required property '"+"points"+"'"};
if(vErrors === null){
vErrors = [err79];
}
else {
vErrors.push(err79);
}
errors++;
}
if(data34.radius === undefined){
const err80 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/path",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/path/required",keyword:"required",params:{missingProperty: "radius"},message:"must have required property '"+"radius"+"'"};
if(vErrors === null){
vErrors = [err80];
}
else {
vErrors.push(err80);
}
errors++;
}
if(data34.variant === undefined){
const err81 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/path",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/path/required",keyword:"required",params:{missingProperty: "variant"},message:"must have required property '"+"variant"+"'"};
if(vErrors === null){
vErrors = [err81];
}
else {
vErrors.push(err81);
}
errors++;
}
for(const key10 in data34){
if(!((((key10 === "points") || (key10 === "radius")) || (key10 === "variant")) || (key10 === "bind"))){
const err82 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/path",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/path/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key10},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err82];
}
else {
vErrors.push(err82);
}
errors++;
}
}
if(data34.points !== undefined){
let data35 = data34.points;
if(Array.isArray(data35)){
if(data35.length > 2){
const err83 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/path/points",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/path/properties/points/maxItems",keyword:"maxItems",params:{limit: 2},message:"must NOT have more than 2 items"};
if(vErrors === null){
vErrors = [err83];
}
else {
vErrors.push(err83);
}
errors++;
}
if(data35.length < 2){
const err84 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/path/points",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/path/properties/points/minItems",keyword:"minItems",params:{limit: 2},message:"must NOT have fewer than 2 items"};
if(vErrors === null){
vErrors = [err84];
}
else {
vErrors.push(err84);
}
errors++;
}
const len7 = data35.length;
for(let i7=0; i7<len7; i7++){
let data36 = data35[i7];
if(Array.isArray(data36)){
if(data36.length > 3){
const err85 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/path/points/" + i7,schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/path/properties/points/items/maxItems",keyword:"maxItems",params:{limit: 3},message:"must NOT have more than 3 items"};
if(vErrors === null){
vErrors = [err85];
}
else {
vErrors.push(err85);
}
errors++;
}
if(data36.length < 3){
const err86 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/path/points/" + i7,schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/path/properties/points/items/minItems",keyword:"minItems",params:{limit: 3},message:"must NOT have fewer than 3 items"};
if(vErrors === null){
vErrors = [err86];
}
else {
vErrors.push(err86);
}
errors++;
}
const len8 = data36.length;
for(let i8=0; i8<len8; i8++){
if(!(typeof data36[i8] == "number")){
const err87 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/path/points/" + i7+"/" + i8,schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/path/properties/points/items/items/type",keyword:"type",params:{type: "number"},message:"must be number"};
if(vErrors === null){
vErrors = [err87];
}
else {
vErrors.push(err87);
}
errors++;
}
}
}
else {
const err88 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/path/points/" + i7,schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/path/properties/points/items/type",keyword:"type",params:{type: "array"},message:"must be array"};
if(vErrors === null){
vErrors = [err88];
}
else {
vErrors.push(err88);
}
errors++;
}
}
}
else {
const err89 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/path/points",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/path/properties/points/type",keyword:"type",params:{type: "array"},message:"must be array"};
if(vErrors === null){
vErrors = [err89];
}
else {
vErrors.push(err89);
}
errors++;
}
}
if(data34.radius !== undefined){
let data38 = data34.radius;
if(typeof data38 == "number"){
if(data38 <= 0 || isNaN(data38)){
const err90 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/path/radius",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/path/properties/radius/exclusiveMinimum",keyword:"exclusiveMinimum",params:{comparison: ">", limit: 0},message:"must be > 0"};
if(vErrors === null){
vErrors = [err90];
}
else {
vErrors.push(err90);
}
errors++;
}
}
else {
const err91 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/path/radius",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/path/properties/radius/type",keyword:"type",params:{type: "number"},message:"must be number"};
if(vErrors === null){
vErrors = [err91];
}
else {
vErrors.push(err91);
}
errors++;
}
}
if(data34.variant !== undefined){
if(!(validate11(data34.variant, {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/path/variant",parentData:data34,parentDataProperty:"variant",rootData}))){
vErrors = vErrors === null ? validate11.errors : vErrors.concat(validate11.errors);
errors = vErrors.length;
}
}
if(data34.bind !== undefined){
let data40 = data34.bind;
if(typeof data40 === "string"){
if(func2(data40) < 1){
const err92 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/path/bind",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/path/properties/bind/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err92];
}
else {
vErrors.push(err92);
}
errors++;
}
}
else {
const err93 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/path/bind",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/path/properties/bind/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err93];
}
else {
vErrors.push(err93);
}
errors++;
}
}
}
else {
const err94 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/path",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/path/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err94];
}
else {
vErrors.push(err94);
}
errors++;
}
}
if(data29.camera !== undefined){
let data41 = data29.camera;
if(data41 && typeof data41 == "object" && !Array.isArray(data41)){
if(data41.target === undefined){
const err95 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/camera",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/camera/required",keyword:"required",params:{missingProperty: "target"},message:"must have required property '"+"target"+"'"};
if(vErrors === null){
vErrors = [err95];
}
else {
vErrors.push(err95);
}
errors++;
}
if(data41.distance === undefined){
const err96 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/camera",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/camera/required",keyword:"required",params:{missingProperty: "distance"},message:"must have required property '"+"distance"+"'"};
if(vErrors === null){
vErrors = [err96];
}
else {
vErrors.push(err96);
}
errors++;
}
if(data41.yaw === undefined){
const err97 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/camera",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/camera/required",keyword:"required",params:{missingProperty: "yaw"},message:"must have required property '"+"yaw"+"'"};
if(vErrors === null){
vErrors = [err97];
}
else {
vErrors.push(err97);
}
errors++;
}
if(data41.pitch === undefined){
const err98 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/camera",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/camera/required",keyword:"required",params:{missingProperty: "pitch"},message:"must have required property '"+"pitch"+"'"};
if(vErrors === null){
vErrors = [err98];
}
else {
vErrors.push(err98);
}
errors++;
}
for(const key11 in data41){
if(!((((key11 === "target") || (key11 === "distance")) || (key11 === "yaw")) || (key11 === "pitch"))){
const err99 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/camera",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/camera/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key11},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err99];
}
else {
vErrors.push(err99);
}
errors++;
}
}
if(data41.target !== undefined){
let data42 = data41.target;
if(Array.isArray(data42)){
if(data42.length > 3){
const err100 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/camera/target",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/camera/properties/target/maxItems",keyword:"maxItems",params:{limit: 3},message:"must NOT have more than 3 items"};
if(vErrors === null){
vErrors = [err100];
}
else {
vErrors.push(err100);
}
errors++;
}
if(data42.length < 3){
const err101 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/camera/target",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/camera/properties/target/minItems",keyword:"minItems",params:{limit: 3},message:"must NOT have fewer than 3 items"};
if(vErrors === null){
vErrors = [err101];
}
else {
vErrors.push(err101);
}
errors++;
}
const len9 = data42.length;
for(let i9=0; i9<len9; i9++){
if(!(typeof data42[i9] == "number")){
const err102 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/camera/target/" + i9,schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/camera/properties/target/items/type",keyword:"type",params:{type: "number"},message:"must be number"};
if(vErrors === null){
vErrors = [err102];
}
else {
vErrors.push(err102);
}
errors++;
}
}
}
else {
const err103 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/camera/target",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/camera/properties/target/type",keyword:"type",params:{type: "array"},message:"must be array"};
if(vErrors === null){
vErrors = [err103];
}
else {
vErrors.push(err103);
}
errors++;
}
}
if(data41.distance !== undefined){
let data44 = data41.distance;
if(typeof data44 == "number"){
if(data44 <= 0 || isNaN(data44)){
const err104 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/camera/distance",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/camera/properties/distance/exclusiveMinimum",keyword:"exclusiveMinimum",params:{comparison: ">", limit: 0},message:"must be > 0"};
if(vErrors === null){
vErrors = [err104];
}
else {
vErrors.push(err104);
}
errors++;
}
}
else {
const err105 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/camera/distance",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/camera/properties/distance/type",keyword:"type",params:{type: "number"},message:"must be number"};
if(vErrors === null){
vErrors = [err105];
}
else {
vErrors.push(err105);
}
errors++;
}
}
if(data41.yaw !== undefined){
if(!(typeof data41.yaw == "number")){
const err106 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/camera/yaw",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/camera/properties/yaw/type",keyword:"type",params:{type: "number"},message:"must be number"};
if(vErrors === null){
vErrors = [err106];
}
else {
vErrors.push(err106);
}
errors++;
}
}
if(data41.pitch !== undefined){
let data46 = data41.pitch;
if(typeof data46 == "number"){
if(data46 > 89 || isNaN(data46)){
const err107 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/camera/pitch",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/camera/properties/pitch/maximum",keyword:"maximum",params:{comparison: "<=", limit: 89},message:"must be <= 89"};
if(vErrors === null){
vErrors = [err107];
}
else {
vErrors.push(err107);
}
errors++;
}
if(data46 < 5 || isNaN(data46)){
const err108 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/camera/pitch",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/camera/properties/pitch/minimum",keyword:"minimum",params:{comparison: ">=", limit: 5},message:"must be >= 5"};
if(vErrors === null){
vErrors = [err108];
}
else {
vErrors.push(err108);
}
errors++;
}
}
else {
const err109 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/camera/pitch",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/camera/properties/pitch/type",keyword:"type",params:{type: "number"},message:"must be number"};
if(vErrors === null){
vErrors = [err109];
}
else {
vErrors.push(err109);
}
errors++;
}
}
}
else {
const err110 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world/camera",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/properties/camera/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err110];
}
else {
vErrors.push(err110);
}
errors++;
}
}
}
else {
const err111 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/world",schemaPath:"#/properties/steps/items/properties/phases/items/properties/world/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err111];
}
else {
vErrors.push(err111);
}
errors++;
}
}
if(data20.until !== undefined){
if(!(validate14(data20.until, {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2+"/until",parentData:data20,parentDataProperty:"until",rootData}))){
vErrors = vErrors === null ? validate14.errors : vErrors.concat(validate14.errors);
errors = vErrors.length;
}
}
}
else {
const err112 = {instancePath:instancePath+"/steps/" + i1+"/phases/" + i2,schemaPath:"#/properties/steps/items/properties/phases/items/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err112];
}
else {
vErrors.push(err112);
}
errors++;
}
}
}
else {
const err113 = {instancePath:instancePath+"/steps/" + i1+"/phases",schemaPath:"#/properties/steps/items/properties/phases/type",keyword:"type",params:{type: "array"},message:"must be array"};
if(vErrors === null){
vErrors = [err113];
}
else {
vErrors.push(err113);
}
errors++;
}
}
if(data16.beforeCheck !== undefined){
let data48 = data16.beforeCheck;
if(Array.isArray(data48)){
const len10 = data48.length;
for(let i10=0; i10<len10; i10++){
let data49 = data48[i10];
const _errs108 = errors;
let valid35 = false;
let passing1 = null;
const _errs109 = errors;
if(data49 && typeof data49 == "object" && !Array.isArray(data49)){
if(data49.type === undefined){
const err114 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10,schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/0/required",keyword:"required",params:{missingProperty: "type"},message:"must have required property '"+"type"+"'"};
if(vErrors === null){
vErrors = [err114];
}
else {
vErrors.push(err114);
}
errors++;
}
if(data49.resource === undefined){
const err115 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10,schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/0/required",keyword:"required",params:{missingProperty: "resource"},message:"must have required property '"+"resource"+"'"};
if(vErrors === null){
vErrors = [err115];
}
else {
vErrors.push(err115);
}
errors++;
}
if(data49.bind === undefined){
const err116 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10,schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/0/required",keyword:"required",params:{missingProperty: "bind"},message:"must have required property '"+"bind"+"'"};
if(vErrors === null){
vErrors = [err116];
}
else {
vErrors.push(err116);
}
errors++;
}
if(data49.body === undefined){
const err117 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10,schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/0/required",keyword:"required",params:{missingProperty: "body"},message:"must have required property '"+"body"+"'"};
if(vErrors === null){
vErrors = [err117];
}
else {
vErrors.push(err117);
}
errors++;
}
for(const key12 in data49){
if(!((((key12 === "type") || (key12 === "resource")) || (key12 === "bind")) || (key12 === "body"))){
const err118 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10,schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/0/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key12},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err118];
}
else {
vErrors.push(err118);
}
errors++;
}
}
if(data49.type !== undefined){
if("create" !== data49.type){
const err119 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10+"/type",schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/0/properties/type/const",keyword:"const",params:{allowedValue: "create"},message:"must be equal to constant"};
if(vErrors === null){
vErrors = [err119];
}
else {
vErrors.push(err119);
}
errors++;
}
}
if(data49.resource !== undefined){
let data51 = data49.resource;
if(!((((data51 === "paths") || (data51 === "parcels")) || (data51 === "zones")) || (data51 === "buildings"))){
const err120 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10+"/resource",schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/0/properties/resource/enum",keyword:"enum",params:{allowedValues: schema11.properties.steps.items.properties.beforeCheck.items.oneOf[0].properties.resource.enum},message:"must be equal to one of the allowed values"};
if(vErrors === null){
vErrors = [err120];
}
else {
vErrors.push(err120);
}
errors++;
}
}
if(data49.bind !== undefined){
let data52 = data49.bind;
if(typeof data52 === "string"){
if(func2(data52) < 1){
const err121 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10+"/bind",schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/0/properties/bind/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err121];
}
else {
vErrors.push(err121);
}
errors++;
}
}
else {
const err122 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10+"/bind",schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/0/properties/bind/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err122];
}
else {
vErrors.push(err122);
}
errors++;
}
}
if(data49.body !== undefined){
let data53 = data49.body;
if(data53 && typeof data53 == "object" && !Array.isArray(data53)){
for(const key13 in data53){
if(!(validate11(data53[key13], {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10+"/body/" + key13.replace(/~/g, "~0").replace(/\//g, "~1"),parentData:data53,parentDataProperty:key13,rootData}))){
vErrors = vErrors === null ? validate11.errors : vErrors.concat(validate11.errors);
errors = vErrors.length;
}
}
}
else {
const err123 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10+"/body",schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/0/properties/body/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err123];
}
else {
vErrors.push(err123);
}
errors++;
}
}
}
else {
const err124 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10,schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/0/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err124];
}
else {
vErrors.push(err124);
}
errors++;
}
var _valid1 = _errs109 === errors;
if(_valid1){
valid35 = true;
passing1 = 0;
}
const _errs120 = errors;
if(data49 && typeof data49 == "object" && !Array.isArray(data49)){
if(data49.type === undefined){
const err125 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10,schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/1/required",keyword:"required",params:{missingProperty: "type"},message:"must have required property '"+"type"+"'"};
if(vErrors === null){
vErrors = [err125];
}
else {
vErrors.push(err125);
}
errors++;
}
if(data49.target === undefined){
const err126 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10,schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/1/required",keyword:"required",params:{missingProperty: "target"},message:"must have required property '"+"target"+"'"};
if(vErrors === null){
vErrors = [err126];
}
else {
vErrors.push(err126);
}
errors++;
}
for(const key14 in data49){
if(!((key14 === "type") || (key14 === "target"))){
const err127 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10,schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/1/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key14},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err127];
}
else {
vErrors.push(err127);
}
errors++;
}
}
if(data49.type !== undefined){
if("completeConstruction" !== data49.type){
const err128 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10+"/type",schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/1/properties/type/const",keyword:"const",params:{allowedValue: "completeConstruction"},message:"must be equal to constant"};
if(vErrors === null){
vErrors = [err128];
}
else {
vErrors.push(err128);
}
errors++;
}
}
if(data49.target !== undefined){
let data56 = data49.target;
if(typeof data56 === "string"){
if(func2(data56) < 1){
const err129 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10+"/target",schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/1/properties/target/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err129];
}
else {
vErrors.push(err129);
}
errors++;
}
}
else {
const err130 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10+"/target",schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/1/properties/target/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err130];
}
else {
vErrors.push(err130);
}
errors++;
}
}
}
else {
const err131 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10,schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/1/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err131];
}
else {
vErrors.push(err131);
}
errors++;
}
var _valid1 = _errs120 === errors;
if(_valid1 && valid35){
valid35 = false;
passing1 = [passing1, 1];
}
else {
if(_valid1){
valid35 = true;
passing1 = 1;
}
const _errs126 = errors;
if(data49 && typeof data49 == "object" && !Array.isArray(data49)){
if(data49.type === undefined){
const err132 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10,schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/2/required",keyword:"required",params:{missingProperty: "type"},message:"must have required property '"+"type"+"'"};
if(vErrors === null){
vErrors = [err132];
}
else {
vErrors.push(err132);
}
errors++;
}
for(const key15 in data49){
if(!(key15 === "type")){
const err133 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10,schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/2/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key15},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err133];
}
else {
vErrors.push(err133);
}
errors++;
}
}
if(data49.type !== undefined){
if("reload" !== data49.type){
const err134 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10+"/type",schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/2/properties/type/const",keyword:"const",params:{allowedValue: "reload"},message:"must be equal to constant"};
if(vErrors === null){
vErrors = [err134];
}
else {
vErrors.push(err134);
}
errors++;
}
}
}
else {
const err135 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10,schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/2/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err135];
}
else {
vErrors.push(err135);
}
errors++;
}
var _valid1 = _errs126 === errors;
if(_valid1 && valid35){
valid35 = false;
passing1 = [passing1, 2];
}
else {
if(_valid1){
valid35 = true;
passing1 = 2;
}
const _errs130 = errors;
if(data49 && typeof data49 == "object" && !Array.isArray(data49)){
if(data49.type === undefined){
const err136 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10,schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/3/required",keyword:"required",params:{missingProperty: "type"},message:"must have required property '"+"type"+"'"};
if(vErrors === null){
vErrors = [err136];
}
else {
vErrors.push(err136);
}
errors++;
}
if(data49.scenario === undefined){
const err137 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10,schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/3/required",keyword:"required",params:{missingProperty: "scenario"},message:"must have required property '"+"scenario"+"'"};
if(vErrors === null){
vErrors = [err137];
}
else {
vErrors.push(err137);
}
errors++;
}
if(data49.action === undefined){
const err138 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10,schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/3/required",keyword:"required",params:{missingProperty: "action"},message:"must have required property '"+"action"+"'"};
if(vErrors === null){
vErrors = [err138];
}
else {
vErrors.push(err138);
}
errors++;
}
for(const key16 in data49){
if(!(((key16 === "type") || (key16 === "scenario")) || (key16 === "action"))){
const err139 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10,schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/3/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key16},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err139];
}
else {
vErrors.push(err139);
}
errors++;
}
}
if(data49.type !== undefined){
if("scenarioAction" !== data49.type){
const err140 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10+"/type",schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/3/properties/type/const",keyword:"const",params:{allowedValue: "scenarioAction"},message:"must be equal to constant"};
if(vErrors === null){
vErrors = [err140];
}
else {
vErrors.push(err140);
}
errors++;
}
}
if(data49.scenario !== undefined){
let data59 = data49.scenario;
if(!(((((((data59 === "scenario3") || (data59 === "scenario4")) || (data59 === "trade")) || (data59 === "highway")) || (data59 === "mining")) || (data59 === "tourism")) || (data59 === "reclamation"))){
const err141 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10+"/scenario",schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/3/properties/scenario/enum",keyword:"enum",params:{allowedValues: schema11.properties.steps.items.properties.beforeCheck.items.oneOf[3].properties.scenario.enum},message:"must be equal to one of the allowed values"};
if(vErrors === null){
vErrors = [err141];
}
else {
vErrors.push(err141);
}
errors++;
}
}
if(data49.action !== undefined){
let data60 = data49.action;
if(typeof data60 === "string"){
if(func2(data60) < 1){
const err142 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10+"/action",schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/3/properties/action/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err142];
}
else {
vErrors.push(err142);
}
errors++;
}
}
else {
const err143 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10+"/action",schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/3/properties/action/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err143];
}
else {
vErrors.push(err143);
}
errors++;
}
}
}
else {
const err144 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10,schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf/3/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err144];
}
else {
vErrors.push(err144);
}
errors++;
}
var _valid1 = _errs130 === errors;
if(_valid1 && valid35){
valid35 = false;
passing1 = [passing1, 3];
}
else {
if(_valid1){
valid35 = true;
passing1 = 3;
}
}
}
}
if(!valid35){
const err145 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck/" + i10,schemaPath:"#/properties/steps/items/properties/beforeCheck/items/oneOf",keyword:"oneOf",params:{passingSchemas: passing1},message:"must match exactly one schema in oneOf"};
if(vErrors === null){
vErrors = [err145];
}
else {
vErrors.push(err145);
}
errors++;
}
else {
errors = _errs108;
if(vErrors !== null){
if(_errs108){
vErrors.length = _errs108;
}
else {
vErrors = null;
}
}
}
}
}
else {
const err146 = {instancePath:instancePath+"/steps/" + i1+"/beforeCheck",schemaPath:"#/properties/steps/items/properties/beforeCheck/type",keyword:"type",params:{type: "array"},message:"must be array"};
if(vErrors === null){
vErrors = [err146];
}
else {
vErrors.push(err146);
}
errors++;
}
}
if(data16.complete !== undefined){
if(!(validate14(data16.complete, {instancePath:instancePath+"/steps/" + i1+"/complete",parentData:data16,parentDataProperty:"complete",rootData}))){
vErrors = vErrors === null ? validate14.errors : vErrors.concat(validate14.errors);
errors = vErrors.length;
}
}
if(data16.afterCheck !== undefined){
let data62 = data16.afterCheck;
if(Array.isArray(data62)){
const len11 = data62.length;
for(let i11=0; i11<len11; i11++){
let data63 = data62[i11];
const _errs141 = errors;
let valid43 = false;
let passing2 = null;
const _errs142 = errors;
if(data63 && typeof data63 == "object" && !Array.isArray(data63)){
if(data63.type === undefined){
const err147 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11,schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/0/required",keyword:"required",params:{missingProperty: "type"},message:"must have required property '"+"type"+"'"};
if(vErrors === null){
vErrors = [err147];
}
else {
vErrors.push(err147);
}
errors++;
}
if(data63.resource === undefined){
const err148 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11,schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/0/required",keyword:"required",params:{missingProperty: "resource"},message:"must have required property '"+"resource"+"'"};
if(vErrors === null){
vErrors = [err148];
}
else {
vErrors.push(err148);
}
errors++;
}
if(data63.bind === undefined){
const err149 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11,schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/0/required",keyword:"required",params:{missingProperty: "bind"},message:"must have required property '"+"bind"+"'"};
if(vErrors === null){
vErrors = [err149];
}
else {
vErrors.push(err149);
}
errors++;
}
if(data63.body === undefined){
const err150 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11,schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/0/required",keyword:"required",params:{missingProperty: "body"},message:"must have required property '"+"body"+"'"};
if(vErrors === null){
vErrors = [err150];
}
else {
vErrors.push(err150);
}
errors++;
}
for(const key17 in data63){
if(!((((key17 === "type") || (key17 === "resource")) || (key17 === "bind")) || (key17 === "body"))){
const err151 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11,schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/0/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key17},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err151];
}
else {
vErrors.push(err151);
}
errors++;
}
}
if(data63.type !== undefined){
if("create" !== data63.type){
const err152 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11+"/type",schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/0/properties/type/const",keyword:"const",params:{allowedValue: "create"},message:"must be equal to constant"};
if(vErrors === null){
vErrors = [err152];
}
else {
vErrors.push(err152);
}
errors++;
}
}
if(data63.resource !== undefined){
let data65 = data63.resource;
if(!((((data65 === "paths") || (data65 === "parcels")) || (data65 === "zones")) || (data65 === "buildings"))){
const err153 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11+"/resource",schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/0/properties/resource/enum",keyword:"enum",params:{allowedValues: schema11.properties.steps.items.properties.afterCheck.items.oneOf[0].properties.resource.enum},message:"must be equal to one of the allowed values"};
if(vErrors === null){
vErrors = [err153];
}
else {
vErrors.push(err153);
}
errors++;
}
}
if(data63.bind !== undefined){
let data66 = data63.bind;
if(typeof data66 === "string"){
if(func2(data66) < 1){
const err154 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11+"/bind",schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/0/properties/bind/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err154];
}
else {
vErrors.push(err154);
}
errors++;
}
}
else {
const err155 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11+"/bind",schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/0/properties/bind/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err155];
}
else {
vErrors.push(err155);
}
errors++;
}
}
if(data63.body !== undefined){
let data67 = data63.body;
if(data67 && typeof data67 == "object" && !Array.isArray(data67)){
for(const key18 in data67){
if(!(validate11(data67[key18], {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11+"/body/" + key18.replace(/~/g, "~0").replace(/\//g, "~1"),parentData:data67,parentDataProperty:key18,rootData}))){
vErrors = vErrors === null ? validate11.errors : vErrors.concat(validate11.errors);
errors = vErrors.length;
}
}
}
else {
const err156 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11+"/body",schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/0/properties/body/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err156];
}
else {
vErrors.push(err156);
}
errors++;
}
}
}
else {
const err157 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11,schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/0/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err157];
}
else {
vErrors.push(err157);
}
errors++;
}
var _valid2 = _errs142 === errors;
if(_valid2){
valid43 = true;
passing2 = 0;
}
const _errs153 = errors;
if(data63 && typeof data63 == "object" && !Array.isArray(data63)){
if(data63.type === undefined){
const err158 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11,schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/1/required",keyword:"required",params:{missingProperty: "type"},message:"must have required property '"+"type"+"'"};
if(vErrors === null){
vErrors = [err158];
}
else {
vErrors.push(err158);
}
errors++;
}
if(data63.target === undefined){
const err159 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11,schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/1/required",keyword:"required",params:{missingProperty: "target"},message:"must have required property '"+"target"+"'"};
if(vErrors === null){
vErrors = [err159];
}
else {
vErrors.push(err159);
}
errors++;
}
for(const key19 in data63){
if(!((key19 === "type") || (key19 === "target"))){
const err160 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11,schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/1/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key19},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err160];
}
else {
vErrors.push(err160);
}
errors++;
}
}
if(data63.type !== undefined){
if("completeConstruction" !== data63.type){
const err161 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11+"/type",schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/1/properties/type/const",keyword:"const",params:{allowedValue: "completeConstruction"},message:"must be equal to constant"};
if(vErrors === null){
vErrors = [err161];
}
else {
vErrors.push(err161);
}
errors++;
}
}
if(data63.target !== undefined){
let data70 = data63.target;
if(typeof data70 === "string"){
if(func2(data70) < 1){
const err162 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11+"/target",schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/1/properties/target/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err162];
}
else {
vErrors.push(err162);
}
errors++;
}
}
else {
const err163 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11+"/target",schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/1/properties/target/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err163];
}
else {
vErrors.push(err163);
}
errors++;
}
}
}
else {
const err164 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11,schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/1/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err164];
}
else {
vErrors.push(err164);
}
errors++;
}
var _valid2 = _errs153 === errors;
if(_valid2 && valid43){
valid43 = false;
passing2 = [passing2, 1];
}
else {
if(_valid2){
valid43 = true;
passing2 = 1;
}
const _errs159 = errors;
if(data63 && typeof data63 == "object" && !Array.isArray(data63)){
if(data63.type === undefined){
const err165 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11,schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/2/required",keyword:"required",params:{missingProperty: "type"},message:"must have required property '"+"type"+"'"};
if(vErrors === null){
vErrors = [err165];
}
else {
vErrors.push(err165);
}
errors++;
}
for(const key20 in data63){
if(!(key20 === "type")){
const err166 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11,schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/2/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key20},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err166];
}
else {
vErrors.push(err166);
}
errors++;
}
}
if(data63.type !== undefined){
if("reload" !== data63.type){
const err167 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11+"/type",schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/2/properties/type/const",keyword:"const",params:{allowedValue: "reload"},message:"must be equal to constant"};
if(vErrors === null){
vErrors = [err167];
}
else {
vErrors.push(err167);
}
errors++;
}
}
}
else {
const err168 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11,schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/2/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err168];
}
else {
vErrors.push(err168);
}
errors++;
}
var _valid2 = _errs159 === errors;
if(_valid2 && valid43){
valid43 = false;
passing2 = [passing2, 2];
}
else {
if(_valid2){
valid43 = true;
passing2 = 2;
}
const _errs163 = errors;
if(data63 && typeof data63 == "object" && !Array.isArray(data63)){
if(data63.type === undefined){
const err169 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11,schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/3/required",keyword:"required",params:{missingProperty: "type"},message:"must have required property '"+"type"+"'"};
if(vErrors === null){
vErrors = [err169];
}
else {
vErrors.push(err169);
}
errors++;
}
if(data63.scenario === undefined){
const err170 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11,schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/3/required",keyword:"required",params:{missingProperty: "scenario"},message:"must have required property '"+"scenario"+"'"};
if(vErrors === null){
vErrors = [err170];
}
else {
vErrors.push(err170);
}
errors++;
}
if(data63.action === undefined){
const err171 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11,schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/3/required",keyword:"required",params:{missingProperty: "action"},message:"must have required property '"+"action"+"'"};
if(vErrors === null){
vErrors = [err171];
}
else {
vErrors.push(err171);
}
errors++;
}
for(const key21 in data63){
if(!(((key21 === "type") || (key21 === "scenario")) || (key21 === "action"))){
const err172 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11,schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/3/additionalProperties",keyword:"additionalProperties",params:{additionalProperty: key21},message:"must NOT have additional properties"};
if(vErrors === null){
vErrors = [err172];
}
else {
vErrors.push(err172);
}
errors++;
}
}
if(data63.type !== undefined){
if("scenarioAction" !== data63.type){
const err173 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11+"/type",schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/3/properties/type/const",keyword:"const",params:{allowedValue: "scenarioAction"},message:"must be equal to constant"};
if(vErrors === null){
vErrors = [err173];
}
else {
vErrors.push(err173);
}
errors++;
}
}
if(data63.scenario !== undefined){
let data73 = data63.scenario;
if(!(((((((data73 === "scenario3") || (data73 === "scenario4")) || (data73 === "trade")) || (data73 === "highway")) || (data73 === "mining")) || (data73 === "tourism")) || (data73 === "reclamation"))){
const err174 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11+"/scenario",schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/3/properties/scenario/enum",keyword:"enum",params:{allowedValues: schema11.properties.steps.items.properties.afterCheck.items.oneOf[3].properties.scenario.enum},message:"must be equal to one of the allowed values"};
if(vErrors === null){
vErrors = [err174];
}
else {
vErrors.push(err174);
}
errors++;
}
}
if(data63.action !== undefined){
let data74 = data63.action;
if(typeof data74 === "string"){
if(func2(data74) < 1){
const err175 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11+"/action",schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/3/properties/action/minLength",keyword:"minLength",params:{limit: 1},message:"must NOT have fewer than 1 characters"};
if(vErrors === null){
vErrors = [err175];
}
else {
vErrors.push(err175);
}
errors++;
}
}
else {
const err176 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11+"/action",schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/3/properties/action/type",keyword:"type",params:{type: "string"},message:"must be string"};
if(vErrors === null){
vErrors = [err176];
}
else {
vErrors.push(err176);
}
errors++;
}
}
}
else {
const err177 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11,schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf/3/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err177];
}
else {
vErrors.push(err177);
}
errors++;
}
var _valid2 = _errs163 === errors;
if(_valid2 && valid43){
valid43 = false;
passing2 = [passing2, 3];
}
else {
if(_valid2){
valid43 = true;
passing2 = 3;
}
}
}
}
if(!valid43){
const err178 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck/" + i11,schemaPath:"#/properties/steps/items/properties/afterCheck/items/oneOf",keyword:"oneOf",params:{passingSchemas: passing2},message:"must match exactly one schema in oneOf"};
if(vErrors === null){
vErrors = [err178];
}
else {
vErrors.push(err178);
}
errors++;
}
else {
errors = _errs141;
if(vErrors !== null){
if(_errs141){
vErrors.length = _errs141;
}
else {
vErrors = null;
}
}
}
}
}
else {
const err179 = {instancePath:instancePath+"/steps/" + i1+"/afterCheck",schemaPath:"#/properties/steps/items/properties/afterCheck/type",keyword:"type",params:{type: "array"},message:"must be array"};
if(vErrors === null){
vErrors = [err179];
}
else {
vErrors.push(err179);
}
errors++;
}
}
}
else {
const err180 = {instancePath:instancePath+"/steps/" + i1,schemaPath:"#/properties/steps/items/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err180];
}
else {
vErrors.push(err180);
}
errors++;
}
}
}
else {
const err181 = {instancePath:instancePath+"/steps",schemaPath:"#/properties/steps/type",keyword:"type",params:{type: "array"},message:"must be array"};
if(vErrors === null){
vErrors = [err181];
}
else {
vErrors.push(err181);
}
errors++;
}
}
}
else {
const err182 = {instancePath,schemaPath:"#/type",keyword:"type",params:{type: "object"},message:"must be object"};
if(vErrors === null){
vErrors = [err182];
}
else {
vErrors.push(err182);
}
errors++;
}
validate10.errors = vErrors;
return errors === 0;
}

