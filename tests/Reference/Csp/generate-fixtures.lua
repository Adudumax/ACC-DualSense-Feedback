-- Executes the unmodified CSP colour function and the actual updateDualSense
-- function body. Only CSP host APIs (colour containers, Sim, Register) are stubbed.
local function rgb(r, g, b)
  local v = {r=r or 0, g=g or 0, b=b or 0}
  function v:set(x) self.r=x.r; self.g=x.g; self.b=x.b; return self end
  function v:scale(x) self.r=self.r*x; self.g=self.g*x; self.b=self.b*x; return self end
  return v
end
_G.rgb = setmetatable({colors={red=rgb(1,0,0),yellow=rgb(1,1,0),green=rgb(0,1,0)}}, {__call=function(_,...) return rgb(...) end})
local function rgbm(r,g,b,m)
  local v = {rgb=rgb(r,g,b), mult=m or 1}
  function v:set(x) self.rgb:set(x.rgb); self.mult=x.mult; return self end
  function v:scale(x) self.rgb:scale(x); return self end
  function v:setLerp(a,b,x)
    self.rgb.r=a.rgb.r+(b.rgb.r-a.rgb.r)*x
    self.rgb.g=a.rgb.g+(b.rgb.g-a.rgb.g)*x
    self.rgb.b=a.rgb.b+(b.rgb.b-a.rgb.b)*x
    self.mult=a.mult+(b.mult-a.mult)*x
    return self
  end
  return v
end
_G.rgbm=setmetatable({colors={red=rgbm(1,0,0,1),green=rgbm(0,1,0,1),black=rgbm(0,0,0,1),
  yellow=rgbm(1,1,0,1),white=rgbm(1,1,1,1),orange=rgbm(1,0.5,0,1),transparent=rgbm(0,0,0,0)}}, {__call=function(_,...) return rgbm(...) end})
function hsv(h,s,v)
  return {rgb=function()
    local c=v*s; local x=c*(1-math.abs(h/60%2-1)); local m=v-c
    if h<60 then return rgb(c+m,x+m,m) else return rgb(x+m,c+m,m) end
  end}
end
function math.saturateN(x) return math.max(0,math.min(1,x)) end
local preciseClock=0
os.preciseClock=function() return preciseClock end
local callbacks={}
function Register(_,f) callbacks[#callbacks+1]=f end
function math.convertHDR(v) return v end
local colourChunk=assert(loadfile('tests/Reference/Csp/led_colors.lua'))
local source=assert(CSP_DUALSENSE_SOURCE)
local body=assert(source:match('local function updateDualSense%(info, ds%)(.-)\n  end'))
local update=assert(load('return function(info,ds) '..body..'\nend'))()
local count=0
local function json(v)
  if type(v)=='string' then return string.format('%q',v) end
  if type(v)=='boolean' or type(v)=='number' then return tostring(v) end
  local rows={}
  for k,x in pairs(v) do rows[#rows+1]=json(k)..':'..json(x) end
  table.sort(rows)
  return '{'..table.concat(rows,',')..'}'
end
local function infoFor(n,ctx)
  return {rpmDown=6000,rpmUp=9000,fuelWarning=1,state={rpm=6000+n*3000,gear=ctx.Gear or 1,
    engineLifeLeft=ctx.EngineFailed and 0 or 1000,hazardLights=ctx.HazardLights or false,
    turningLightsActivePhase=ctx.TurningLightsActivePhase or false,fuel=ctx.LowFuel and 0 or 10,
    anyFlamesActive=ctx.FlamesActive or false,headlightsActive=ctx.HeadlightsActive or false,
    lowBeams=ctx.LowBeams or false,headlightsColor=rgb(0.8,0.9,1)}}
end
local function run(n,mode,phase,ctx)
  callbacks={}; colourChunk()
  settings={LOW_BATTERY_WARNING=true,BACKGROUND_COLORS=false,SESSION_START=true,EXTRA_COLORS=true,SHIFTING_COLORS=mode}
  Sim={isReplayActive=false,isPaused=false,timeToSessionStart=-100000}
  lowBattery=false
  update(infoFor(1.1,{}),{playerLEDs={},lightBar=rgbm(0,0,0,1)})
  for _,f in ipairs(callbacks) do f(phase) end
  Sim.isReplayActive=ctx.IsReplay or false
  Sim.isPaused=ctx.IsPaused or false
  Sim.timeToSessionStart=ctx.TimeToSessionStartMs or -100000
  lowBattery=ctx.LowBattery or false
  preciseClock=ctx.Clock or phase
  local info=infoFor(n,ctx)
  local ds={playerLEDs={},lightBar=rgbm(0,0,0,1)}
  update(info,ds)
  local mask=0
  for i=0,4 do if ds.playerLEDs[i] then mask=mask+2^i end end
  local context={}
  for k,v in pairs(ctx) do if k~='Clock' and k~='Gear' then context[k]=v end end
  if ctx.HeadlightsActive then context.HeadlightsColor={Red=0.8,Green=0.9,Blue=1} end
  local expected={ds.lightBar.rgb.r*ds.lightBar.mult,ds.lightBar.rgb.g*ds.lightBar.mult,ds.lightBar.rgb.b*ds.lightBar.mult,mask,ds.playerLEDsBrightness}
  count=count+1
  local row=string.format('{"Name":"case-%d","Normalized":%.17g,"Mode":"%s","Flashing":%.17g,"Clock":%.17g,"Gear":%d,"Context":%s,"Expected":[%.17g,%.17g,%.17g,%d,%d]}',
    count,(info.state.rpm-info.rpmDown)/(info.rpmUp-info.rpmDown),mode=='SMOOTH' and 'Smooth' or 'Discrete',phase,preciseClock,info.state.gear,json(context),table.unpack(expected))
  io.write(count>1 and ',\n' or '[\n',row)
end
local bounds={-1,-0.500001,-0.5,-0.499999,-0.25,-0.000001,0,0.000001,0.199999,0.2,0.200001,0.249999,0.25,0.250001,
  0.499999,0.5,0.500001,0.599999,0.6,0.600001,0.749999,0.75,0.750001,0.899999,0.9,0.900001,0.999999,1,1.000001,1.1}
for _,mode in ipairs({'SMOOTH','DISCRETE'}) do
  for _,n in ipairs(bounds) do
    for _,phase in ipairs({0,0.1666666,0.1666667,0.3333333,0.5,0.900001}) do run(n,mode,phase,{}) end
  end
  for i=0,100 do run(-1+i*0.021,mode,0,{}) end
  for _,ctx in ipairs({{Gear=-1},{Gear=0},{Gear=6},{IsReplay=true},{LowBattery=true},
    {TimeToSessionStartMs=8000},{TimeToSessionStartMs=6700},{TimeToSessionStartMs=6699},
    {TimeToSessionStartMs=1},{TimeToSessionStartMs=0},{TimeToSessionStartMs=-999},{TimeToSessionStartMs=-1000},
    {HazardLights=true},{HazardLights=true,TurningLightsActivePhase=true},{EngineFailed=true},
    {EngineFailed=true,TurningLightsActivePhase=true},{LowFuel=true},{FlamesActive=true},
    {HeadlightsActive=true},{HeadlightsActive=true,LowBeams=true},
    {Gear=-1,LowFuel=true,HazardLights=true,FlamesActive=true,TurningLightsActivePhase=true},
    {Gear=-1,TimeToSessionStartMs=1},{LowFuel=true,FlamesActive=true},
    {IsReplay=true,TimeToSessionStartMs=1},{IsReplay=true,LowBattery=true}}) do
    for _,phase in ipairs({0,0.2,0.7}) do
      run(-0.25,mode,phase,ctx); run(0.95,mode,phase,ctx)
    end
  end
end
io.write('\n]\n')
io.stderr:write(string.format('%d CSP Lua reference cases generated\n',count))
